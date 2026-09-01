using System.Text.Json;
using System.Text.RegularExpressions;

namespace TodoApp.Eval;

internal sealed record AgentInvocation(ProcessSpec Process, string RawLogName, string StdErrName, string FinalResponseName);

internal sealed record AgentExecution(AgentTelemetry Telemetry, ProcessResult Process, string Response);

internal interface IAgentExecutor
{
    Task<AgentExecution> ExecuteAsync(AgentSpec spec, string workspace, string prompt, string artifactRoot,
        TimeSpan timeout, bool readOnly, string? fakeScript = null, CancellationToken cancellationToken = default);
}

internal sealed class HostAgentExecutor(ProcessRunner processes) : IAgentExecutor
{
    public async Task<AgentExecution> ExecuteAsync(AgentSpec spec, string workspace, string prompt, string artifactRoot,
        TimeSpan timeout, bool readOnly, string? fakeScript = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(artifactRoot);
        var adapter = AgentAdapterFactory.Create(spec.Provider);
        var invocation = adapter.Build(spec, workspace, prompt, artifactRoot, fakeScript);
        if (readOnly) invocation = AgentInvocationPolicy.RestrictToReadOnly(invocation, spec.Provider);
        var version = await processes.TryGetVersionAsync(invocation.Process.FileName, workspace);
        var result = await processes.RunAsync(invocation.Process, timeout,
            Path.Combine(artifactRoot, invocation.RawLogName), Path.Combine(artifactRoot, invocation.StdErrName), cancellationToken);
        ProviderLogCapture.CopyAndRemove(invocation, artifactRoot);
        var telemetry = adapter.Parse(spec, invocation, result, version);
        Json.Write(Path.Combine(artifactRoot, "telemetry.json"), telemetry);
        var finalPath = Path.Combine(artifactRoot, invocation.FinalResponseName);
        var response = File.Exists(finalPath) ? File.ReadAllText(finalPath) : telemetry.FinalResponse ?? result.Stdout;
        return new(telemetry, result, response);
    }
}

internal static class AgentInvocationPolicy
{
    public static AgentInvocation RestrictToReadOnly(AgentInvocation invocation, CliProvider provider)
    {
        var args = invocation.Process.Arguments.ToList();
        if (provider == CliProvider.Codex)
        {
            var index = args.IndexOf("--sandbox"); if (index >= 0 && index + 1 < args.Count) args[index + 1] = "read-only";
        }
        else if (provider == CliProvider.Claude)
        {
            var index = args.IndexOf("--allowedTools"); if (index >= 0 && index + 1 < args.Count) args[index + 1] = "Read,Glob,Grep,Bash(git diff:*),Bash(git status:*)";
        }
        else if (provider == CliProvider.Copilot)
        {
            for (var i = args.Count - 2; i >= 0; i--)
                if (args[i] == "--allow-tool" && (args[i + 1] == "write" || args[i + 1].StartsWith("shell(dotnet", StringComparison.Ordinal))) { args.RemoveAt(i + 1); args.RemoveAt(i); }
        }
        return invocation with { Process = invocation.Process with { Arguments = args } };
    }
}

internal interface IAgentAdapter
{
    AgentInvocation Build(AgentSpec spec, string worktree, string prompt, string artifactDirectory, string? fakeScript = null);
    AgentTelemetry Parse(AgentSpec spec, AgentInvocation invocation, ProcessResult result, string? cliVersion);
}

internal static class AgentAdapterFactory
{
    public static IAgentAdapter Create(CliProvider provider) => provider switch
    {
        CliProvider.Codex => new CodexAdapter(),
        CliProvider.Claude => new ClaudeAdapter(),
        CliProvider.Copilot => new CopilotAdapter(),
        CliProvider.Fake => new FakeAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}

internal abstract class AgentAdapterBase : IAgentAdapter
{
    public abstract AgentInvocation Build(AgentSpec spec, string worktree, string prompt, string artifactDirectory, string? fakeScript = null);
    public abstract AgentTelemetry Parse(AgentSpec spec, AgentInvocation invocation, ProcessResult result, string? cliVersion);

    protected static AgentTelemetry Basic(AgentSpec spec, AgentInvocation invocation, ProcessResult r, string? version,
        string? reportedModel = null, TokenUsage? tokens = null, int? turns = null, int? tools = null,
        int? shells = null, int? failed = null, int? repeated = null, string? final = null,
        int? builds = null, int? tests = null, int? recovery = null, bool? hostSkillsAvailable = null,
        bool? providerUsageExposed = null, IReadOnlyList<string>? observedSkillNames = null,
        IReadOnlyList<string>? observedShellCommands = null)
    {
        builds ??= CountCommands(r.Stdout, "dotnet build");
        tests ??= CountCommands(r.Stdout, "dotnet test");
        return new(spec.Provider.ToString().ToLowerInvariant(), spec.Model, spec.ReasoningEffort, reportedModel, version,
            [invocation.Process.FileName, .. invocation.Process.Arguments], r.StartedAt, r.FinishedAt,
            r.Duration.TotalSeconds, r.ExitCode, r.TimedOut, r.StdoutBytes, r.StderrBytes,
            tokens ?? new(null, null, null, null), turns, tools, shells, failed, repeated, builds, tests,
            recovery ?? (failed is > 0 ? failed : null), hostSkillsAvailable, providerUsageExposed,
            observedSkillNames, final, final is null ? null : ClaimsUnobservedTests(final, tests.Value), observedShellCommands);
    }

    private static int CountCommands(string text, string value) => Regex.Matches(text, Regex.Escape(value), RegexOptions.IgnoreCase).Count;
    private static bool ClaimsUnobservedTests(string final, int observed) =>
        observed == 0 && Regex.IsMatch(final, @"\btests?\b.*\b(pass|passed|passing|green|succeed)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
}

internal sealed class CodexAdapter : AgentAdapterBase
{
    public override AgentInvocation Build(AgentSpec spec, string worktree, string prompt, string artifactDirectory, string? fakeScript = null)
    {
        ReasoningEfforts.Require(spec.ReasoningEffort, "Codex");
        return new(new(spec.Executable ?? "codex",
            ["exec", "--sandbox", "workspace-write", "--ephemeral", "--json", "--color", "never", "--model", spec.Model,
             "-c", $"model_reasoning_effort=\"{spec.ReasoningEffort}\"", "--cd", worktree, "-"], worktree, prompt),
            "events.jsonl", "agent.stderr.log", "final-response.txt");
    }

    public override AgentTelemetry Parse(AgentSpec spec, AgentInvocation invocation, ProcessResult r, string? version)
    {
        long input = 0, cached = 0, output = 0, reasoning = 0;
        var hasInput = true; var hasCached = true; var hasOutput = true; var hasReasoning = true;
        string? model = null; string? final = null; var turns = 0;
        var turnIds = new HashSet<string>(StringComparer.Ordinal);
        var itemsById = new Dictionary<string, ToolObservation>(StringComparer.Ordinal);
        var terminalItemsWithoutId = new List<ToolObservation>();
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = String(root, "type") ?? "";
                model ??= String(root, "model");

                if (type == "turn.completed")
                {
                    var turnId = String(root, "id") ?? String(root, "turn_id");
                    if (turnId is not null && !turnIds.Add(turnId)) continue;
                    turns++;
                    if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                    {
                        hasInput = hasCached = hasOutput = hasReasoning = false;
                    }
                    else
                    {
                        AddUsage(usage, "input_tokens", ref input, ref hasInput);
                        AddUsage(usage, "cached_input_tokens", ref cached, ref hasCached);
                        AddUsage(usage, "output_tokens", ref output, ref hasOutput);
                        AddReasoningUsage(usage, ref reasoning, ref hasReasoning);
                    }
                }

                if (root.TryGetProperty("item", out var item))
                {
                    var itemType = String(item, "type") ?? "";
                    if (itemType is "command_execution" or "mcp_tool_call" or "file_change")
                    {
                        var terminal = IsTerminal(type, item);
                        var observation = new ToolObservation(itemType, String(item, "command"), terminal,
                            terminal && (type == "item.failed" || String(item, "status") == "failed"));
                        var itemId = String(item, "id") ?? String(root, "item_id");
                        if (itemId is not null)
                        {
                            if (itemsById.TryGetValue(itemId, out var prior)) observation = prior.Merge(observation);
                            itemsById[itemId] = observation;
                        }
                        else if (terminal)
                        {
                            terminalItemsWithoutId.Add(observation);
                        }
                    }
                    if (itemType == "agent_message") final = String(item, "text") ?? final;
                }
            }
            catch (JsonException) { }
        }
        var observations = itemsById.Values.Concat(terminalItemsWithoutId).ToArray();
        var commands = observations.Where(x => x.Type == "command_execution" && x.Command is not null)
            .Select(x => x.Command!).ToArray();
        var tools = observations.Length;
        var shells = observations.Count(x => x.Type == "command_execution");
        var failed = observations.Count(x => x.TerminalFailed);
        var repeated = commands.GroupBy(x => x, StringComparer.Ordinal).Sum(g => Math.Max(0, g.Count() - 1));
        var builds = CountInvocations(commands, @"\bdotnet\s+build\b");
        var tests = CountInvocations(commands, @"\bdotnet\s+test\b");
        var hasCompletedTurn = turns > 0;
        return Basic(spec, invocation, r, version, model,
            new(hasCompletedTurn && hasInput ? input : null, hasCompletedTurn && hasCached ? cached : null,
                hasCompletedTurn && hasOutput ? output : null, hasCompletedTurn && hasReasoning ? reasoning : null),
            hasCompletedTurn ? turns : null, tools, shells, failed, repeated, final,
            builds, tests, failed, !invocation.Process.Arguments.Contains("--ignore-user-config"), false, null, commands);
    }

    private static string? String(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static bool IsTerminal(string eventType, JsonElement item)
    {
        if (eventType is "item.completed" or "item.failed") return true;
        return String(item, "status") is "completed" or "failed" or "cancelled";
    }

    private static void AddUsage(JsonElement usage, string name, ref long total, ref bool available)
    {
        if (usage.TryGetProperty(name, out var property) && property.TryGetInt64(out var value)) total += value;
        else available = false;
    }

    private static void AddReasoningUsage(JsonElement usage, ref long total, ref bool available)
    {
        if ((usage.TryGetProperty("reasoning_output_tokens", out var property) || usage.TryGetProperty("reasoning_tokens", out property))
            && property.TryGetInt64(out var value)) total += value;
        else available = false;
    }

    private static int CountInvocations(IEnumerable<string> commands, string pattern) =>
        commands.Sum(command => Regex.Matches(command, pattern, RegexOptions.IgnoreCase).Count);

    private sealed record ToolObservation(string Type, string? Command, bool Terminal, bool TerminalFailed)
    {
        public ToolObservation Merge(ToolObservation later) => new(
            later.Type,
            later.Command ?? Command,
            Terminal || later.Terminal,
            TerminalFailed || later.TerminalFailed);
    }
}

internal static class ReasoningEfforts
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "none", "low", "medium", "high", "xhigh", "max"
    };

    public static bool IsValid(string? value) => value is not null && Supported.Contains(value);

    public static void Require(string? value, string role)
    {
        if (!IsValid(value))
            throw new HarnessException($"{role} reasoning effort must be one of: none, low, medium, high, xhigh, max.");
    }
}

internal sealed class ClaudeAdapter : AgentAdapterBase
{
    public override AgentInvocation Build(AgentSpec spec, string worktree, string prompt, string artifactDirectory, string? fakeScript = null) =>
        new(new(spec.Executable ?? "claude",
            ["--print", "--output-format", "stream-json", "--verbose", "--no-session-persistence", "--permission-mode", "acceptEdits",
             "--allowedTools", "Read,Edit,Write,Glob,Grep,Bash(dotnet:*),Bash(git status:*),Bash(git diff:*)", "--model", spec.Model, prompt], worktree),
            "events.jsonl", "agent.stderr.log", "final-response.txt");

    public override AgentTelemetry Parse(AgentSpec spec, AgentInvocation invocation, ProcessResult r, string? version)
    {
        long input = 0, cached = 0, output = 0; var seen = false; var turns = 0; var tools = 0; var failed = 0; string? model = null; string? final = null;
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                model ??= root.TryGetProperty("model", out var m) ? m.GetString() : null;
                if (root.TryGetProperty("type", out var t) && t.GetString() == "assistant") turns++;
                if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String) final = result.GetString();
                if (root.TryGetProperty("usage", out var usage))
                {
                    seen = true; input += Num(usage, "input_tokens"); cached += Num(usage, "cache_read_input_tokens"); output += Num(usage, "output_tokens");
                }
                var json = root.GetRawText(); tools += Regex.Matches(json, "tool_use").Count; failed += Regex.Matches(json, "is_error\\\":true").Count;
            }
            catch (JsonException) { }
        }
        return Basic(spec, invocation, r, version, model, new(seen ? input : null, seen ? cached : null, seen ? output : null, null), turns, tools, null, failed, null, final);
    }

    private static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.TryGetInt64(out var n) ? n : 0;
}

internal sealed class CopilotAdapter : AgentAdapterBase
{
    public override AgentInvocation Build(AgentSpec spec, string worktree, string prompt, string artifactDirectory, string? fakeScript = null) =>
        new(new(spec.Executable ?? "copilot",
            ["--prompt", prompt, "--model", spec.Model, "--no-color", "--stream", "on", "--no-ask-user", "--log-dir", Path.Combine(Path.GetTempPath(), "todoapp-eval-copilot", Guid.NewGuid().ToString("N")),
             "--disable-builtin-mcps", "--disallow-temp-dir", "--allow-tool", "write", "--allow-tool", "shell(dotnet:*)",
             "--allow-tool", "shell(git status:*)", "--allow-tool", "shell(git diff:*)"], worktree),
            "agent.stdout.log", "agent.stderr.log", "final-response.txt");

    public override AgentTelemetry Parse(AgentSpec spec, AgentInvocation invocation, ProcessResult r, string? version) =>
        Basic(spec, invocation, r, version, final: r.Stdout);
}

internal sealed class FakeAdapter : AgentAdapterBase
{
    public override AgentInvocation Build(AgentSpec spec, string worktree, string prompt, string artifactDirectory, string? fakeScript = null)
    {
        if (fakeScript is null && spec.Executable is null)
            throw new HarnessException("The fake adapter requires --fake-script or a role-specific --*-executable.");
        var executable = spec.Executable ?? "/bin/sh";
        IReadOnlyList<string> arguments = fakeScript is null ? [] : [fakeScript];
        return new(new(executable, arguments, worktree, Environment: new Dictionary<string, string?> { ["EVAL_PROMPT"] = prompt }),
            "agent.stdout.log", "agent.stderr.log", "final-response.txt");
    }

    public override AgentTelemetry Parse(AgentSpec spec, AgentInvocation invocation, ProcessResult r, string? version) => Basic(spec, invocation, r, version, final: r.Stdout);
}

internal static class ProviderLogCapture
{
    public static void CopyAndRemove(AgentInvocation invocation, string artifactRoot)
    {
        var args = invocation.Process.Arguments.ToList(); var index = args.IndexOf("--log-dir");
        if (index < 0 || index + 1 >= args.Count) return;
        var source = args[index + 1]; if (!Directory.Exists(source)) return;
        var target = Path.Combine(artifactRoot, "provider-logs"); Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, true);
        }
        Directory.Delete(source, recursive: true);
    }
}
