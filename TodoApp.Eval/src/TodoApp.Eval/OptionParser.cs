namespace TodoApp.Eval;

internal static class OptionParser
{
    public static (string Command, Dictionary<string, string?> Values) Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") return ("help", []);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new HarnessException($"Unexpected argument: {args[i]}");
            var key = args[i][2..];
            if (key is "cleanup") { values[key] = "true"; continue; }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new HarnessException($"Option --{key} requires a value.");
            values[key] = args[++i];
        }
        return (args[0].ToLowerInvariant(), values);
    }

    public static EvalOptions Eval(Dictionary<string, string?> v)
    {
        RejectLegacyJudgeOptions(v);
        var repository = Required(v, "repo");
        var baseCommit = Required(v, "base");
        var implementation = Agent(v, "implementation", required: true)
            ?? throw new HarnessException("Implementation agent is required.");
        var graderModel = v.GetValueOrDefault("grader-model") ?? "gpt-5.6-terra";
        var graderEffort = v.GetValueOrDefault("grader-reasoning-effort") ?? "high";
        ReasoningEfforts.Require(graderEffort, "Semantic grader");
        if ((graderModel, graderEffort) is not ("gpt-5.6-terra", "high") and not ("gpt-5.6-sol", "high"))
            throw new HarnessException("Semantic grading supports gpt-5.6-terra/high or the temporary gpt-5.6-sol/high comparison configuration.");
        var grader = new AgentSpec(CliProvider.Codex, graderModel, ReasoningEffort: graderEffort);
        var controllerRoot = AppContext.BaseDirectory;
        var isolation = ParseIsolation(v.GetValueOrDefault("isolation"), implementation.Provider);
        var auth = Path.GetFullPath(v.GetValueOrDefault("codex-auth")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json"));
        return new(
            Path.GetFullPath(repository), baseCommit, implementation, grader,
            Path.GetFullPath(v.GetValueOrDefault("output") ?? Path.Combine(controllerRoot, "results")),
            Path.GetFullPath(v.GetValueOrDefault("worktrees") ?? Path.Combine(controllerRoot, "worktrees")),
            TimeSpan.FromMinutes(int.TryParse(v.GetValueOrDefault("timeout-minutes"), out var timeout) ? timeout : 30),
            v.ContainsKey("cleanup"), v.GetValueOrDefault("run-id"), v.GetValueOrDefault("fake-script"), isolation,
            v.GetValueOrDefault("agent-image") ?? "todoapp-eval-agent:local",
            v.GetValueOrDefault("evaluator-image") ?? "todoapp-eval-evaluator:local", auth,
            v.GetValueOrDefault("review-profile") ?? "agentic-v2");
    }

    private static IsolationKind ParseIsolation(string? value, CliProvider provider)
    {
        if (value is null) return provider == CliProvider.Fake ? IsolationKind.Host : IsolationKind.Container;
        return Enum.TryParse<IsolationKind>(value, true, out var isolation)
            ? isolation
            : throw new HarnessException("--isolation must be 'container' or 'host'.");
    }

    private static AgentSpec? Agent(Dictionary<string, string?> v, string prefix, bool required)
    {
        var cli = v.GetValueOrDefault(prefix + "-cli");
        var model = v.GetValueOrDefault(prefix + "-model");
        if (!required && cli is null && model is null) return null;
        if (cli is null || model is null) throw new HarnessException($"--{prefix}-cli and --{prefix}-model must be supplied together.");
        if (!Enum.TryParse<CliProvider>(cli, true, out var provider))
            throw new HarnessException($"Unknown CLI '{cli}'. Expected codex, claude, copilot, or fake.");
        var effort = v.GetValueOrDefault(prefix + "-reasoning-effort");
        if (provider == CliProvider.Codex)
        {
            if (effort is null)
                throw new HarnessException($"Missing required option --{prefix}-reasoning-effort.");
            ReasoningEfforts.Require(effort, prefix);
        }
        else if (effort is not null)
        {
            throw new HarnessException($"--{prefix}-reasoning-effort is supported only for Codex.");
        }
        return new(provider, model, v.GetValueOrDefault(prefix + "-executable"), effort);
    }

    private static void RejectLegacyJudgeOptions(IReadOnlyDictionary<string, string?> values)
    {
        var obsolete = values.Keys.FirstOrDefault(key =>
            (key.StartsWith("review-", StringComparison.OrdinalIgnoreCase) && !key.Equals("review-profile", StringComparison.OrdinalIgnoreCase)) ||
            key.Equals("grader-cli", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("grader-executable", StringComparison.OrdinalIgnoreCase));
        if (obsolete is not null)
            throw new HarnessException($"--{obsolete} is no longer supported; semantic grading uses one fixed Codex configuration.");
    }

    private static string Required(Dictionary<string, string?> v, string key) =>
        v.GetValueOrDefault(key) ?? throw new HarnessException($"Missing required option --{key}.");
}
