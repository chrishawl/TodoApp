using System.Formats.Tar;
using System.Globalization;

namespace TodoApp.Eval;

internal sealed record EvalWorkspace(string Path, string BaseCommit, string? Branch);

internal interface IEvaluationRuntime : IAsyncDisposable
{
    IAgentExecutor Agents { get; }
    ContainerEnvironment? Container { get; }
    Task<string> GetDotnetSdkAsync(string repository);
    Task<EvalWorkspace> CreateWorkspaceAsync(string repository, string target, string commit, string? branch);
    Task RemoveWorkspaceAsync(string repository, EvalWorkspace workspace);
    Task<bool> RestoreAsync(EvalWorkspace workspace, string log, TimeSpan timeout);
    Task<DeterministicResult> EvaluateAsync(EvalWorkspace workspace, string artifactRoot, TimeSpan timeout,
        DeterministicResult? baseline, string? baselinePath, bool includePrivateTests);
}

internal sealed class HostEvaluationRuntime(ProcessRunner processes) : IEvaluationRuntime
{
    private readonly GitWorkspace git = new(processes);

    public IAgentExecutor Agents { get; } = new HostAgentExecutor(processes);
    public ContainerEnvironment? Container => null;

    public async Task<string> GetDotnetSdkAsync(string repository) =>
        (await processes.RunAsync(new("dotnet", ["--version"], repository), TimeSpan.FromMinutes(1))).Stdout.Trim();

    public async Task<EvalWorkspace> CreateWorkspaceAsync(string repository, string target, string commit, string? branch)
    {
        await git.CreateAsync(repository, target, commit, branch);
        return new(target, commit, branch);
    }

    public Task RemoveWorkspaceAsync(string repository, EvalWorkspace workspace) =>
        git.RemoveAsync(repository, workspace.Path, workspace.Branch);

    public async Task<bool> RestoreAsync(EvalWorkspace workspace, string log, TimeSpan timeout)
    {
        var result = await processes.RunAsync(new("dotnet", ["restore", Path.Combine(workspace.Path, "TodoApp.sln"), "--nologo"], workspace.Path),
            timeout, log, log + ".stderr");
        return result.ExitCode == 0 && !result.TimedOut;
    }

    public async Task<DeterministicResult> EvaluateAsync(EvalWorkspace workspace, string artifactRoot, TimeSpan timeout,
        DeterministicResult? baseline, string? baselinePath, bool includePrivateTests)
    {
        var result = await new DeterministicEvaluator(processes, git, AppContext.BaseDirectory)
            .EvaluateAsync(workspace.Path, workspace.BaseCommit, artifactRoot, timeout, baseline, includePrivateTests);
        File.WriteAllText(Path.Combine(artifactRoot, "patch.diff"), await git.CapturePatchAsync(workspace.Path, workspace.BaseCommit));
        return result;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class DockerEvaluationRuntime : IEvaluationRuntime
{
    internal const string AuthVolume = "todoapp-eval-codex-auth-v1";
    internal const string WorkspaceMount = "/workspace";
    internal const string CodexHome = "/codex-home";
    internal const string Network = "bridge";
    internal const string MemoryLimit = "6g";
    internal const double CpuLimit = 4;
    internal const int PidsLimit = 1024;

    private readonly ProcessRunner processes;
    private readonly EvalOptions options;
    private readonly string nugetVolume;
    private readonly DockerCodexExecutor agents;
    private int sequence;

    private DockerEvaluationRuntime(ProcessRunner processes, EvalOptions options, string runId,
        string agentImageId, string evaluatorImageId)
    {
        this.processes = processes;
        this.options = options;
        nugetVolume = "todoapp-eval-nuget-" + runId.ToLowerInvariant();
        Container = new(options.AgentImage, agentImageId, options.EvaluatorImage, evaluatorImageId,
            AuthVolume, nugetVolume, WorkspaceMount, CodexHome, Network, MemoryLimit, CpuLimit, PidsLimit,
            ReadOnlyRoot: true, CapabilitiesDropped: true, NoNewPrivileges: true,
            UserConfigIgnored: true, SessionEphemeral: true);
        agents = new DockerCodexExecutor(this);
    }

    public IAgentExecutor Agents => agents;
    public ContainerEnvironment Container { get; }

    public static async Task<DockerEvaluationRuntime> CreateAsync(ProcessRunner processes, EvalOptions options, string runId)
    {
        if (options.Implementation.Provider != CliProvider.Codex || options.SemanticGrader.Provider != CliProvider.Codex)
            throw new HarnessException("Container isolation currently supports Codex for implementation and semantic grading roles.");
        if (!File.Exists(options.CodexAuthFile))
            throw new HarnessException($"Codex authentication file was not found: {options.CodexAuthFile}. Run 'codex login' on the host first.");

        var docker = await processes.RunAsync(new("docker", ["version", "--format", "{{.Server.Version}}"], Environment.CurrentDirectory), TimeSpan.FromMinutes(1));
        if (docker.ExitCode != 0) throw new HarnessException("Docker is unavailable: " + docker.Stderr.Trim());
        var agentId = await InspectImage(processes, options.AgentImage);
        var evaluatorId = await InspectImage(processes, options.EvaluatorImage);
        var runtime = new DockerEvaluationRuntime(processes, options, runId, agentId, evaluatorId);
        await runtime.CreateVolumeAsync(AuthVolume);
        await runtime.CreateVolumeAsync(runtime.nugetVolume);
        await runtime.SeedAuthenticationAsync();
        await runtime.VerifyAuthenticationAsync();
        return runtime;
    }

    public async Task<string> GetDotnetSdkAsync(string repository)
    {
        var result = await RunEvaluatorAsync(null, null, ["sdk-version"], TimeSpan.FromMinutes(1));
        RequireSuccess(result, "read the evaluator SDK version");
        return result.Stdout.Trim();
    }

    public async Task<EvalWorkspace> CreateWorkspaceAsync(string repository, string target, string commit, string? branch)
    {
        if (Directory.Exists(target)) throw new HarnessException($"Workspace path already exists: {target}");
        Directory.CreateDirectory(target);
        var archive = Path.Combine(Path.GetTempPath(), "todoapp-eval-" + Guid.NewGuid().ToString("N") + ".tar");
        try
        {
            var archived = await processes.RunAsync(new("git", ["archive", "--format=tar", "--output", archive, commit], repository), TimeSpan.FromMinutes(3));
            RequireSuccess(archived, "export the pinned repository snapshot");
            TarFile.ExtractToDirectory(archive, target, overwriteFiles: false);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
        }

        await Git(target, ["init", "--quiet"]);
        await Git(target, ["config", "user.email", "eval@example.invalid"]);
        await Git(target, ["config", "user.name", "TodoApp Eval"]);
        await Git(target, ["add", "--all"]);
        var commitResult = await processes.RunAsync(new("git", ["commit", "--quiet", "-m", "Pinned evaluation snapshot"], target,
            Environment: new Dictionary<string, string?>
            {
                ["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z",
                ["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z"
            }), TimeSpan.FromMinutes(3));
        RequireSuccess(commitResult, "commit the isolated repository snapshot");
        if (branch is not null) await Git(target, ["branch", "-M", branch]);
        var localBase = await Git(target, ["rev-parse", "HEAD"]);
        return new(target, localBase.Stdout.Trim(), branch);
    }

    public Task RemoveWorkspaceAsync(string repository, EvalWorkspace workspace)
    {
        var target = Path.GetFullPath(workspace.Path);
        var root = Path.GetFullPath(options.WorktreeRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.Ordinal) || target == root.TrimEnd(Path.DirectorySeparatorChar))
            throw new HarnessException($"Refusing to remove workspace outside the configured root: {target}");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        return Task.CompletedTask;
    }

    public async Task<bool> RestoreAsync(EvalWorkspace workspace, string log, TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var result = await RunEvaluatorAsync(workspace.Path, Path.GetDirectoryName(log),
            ["restore", "--workspace", WorkspaceMount, "--log", "/artifacts/" + Path.GetFileName(log), "--timeout-minutes", Minutes(timeout)], timeout);
        File.WriteAllText(log + ".container.stderr", result.Stderr);
        return result.ExitCode == 0 && !result.TimedOut;
    }

    public async Task<DeterministicResult> EvaluateAsync(EvalWorkspace workspace, string artifactRoot, TimeSpan timeout,
        DeterministicResult? baseline, string? baselinePath, bool includePrivateTests)
    {
        Directory.CreateDirectory(artifactRoot);
        var args = new List<string>
        {
            "evaluate", "--workspace", WorkspaceMount, "--base", workspace.BaseCommit,
            "--artifacts", "/artifacts", "--timeout-minutes", Minutes(timeout)
        };
        if (includePrivateTests) args.Add("--private");
        if (baselinePath is not null) { args.Add("--baseline"); args.Add("/baseline.json"); }
        var result = await RunEvaluatorAsync(workspace.Path, artifactRoot, args, timeout + TimeSpan.FromMinutes(2), baselinePath);
        File.WriteAllText(Path.Combine(artifactRoot, "evaluator.stdout.log"), result.Stdout);
        File.WriteAllText(Path.Combine(artifactRoot, "evaluator.stderr.log"), result.Stderr);
        RequireSuccess(result, "run deterministic evaluation");
        return Json.Read<DeterministicResult>(Path.Combine(artifactRoot, "deterministic.json"));
    }

    internal async Task<AgentExecution> ExecuteCodexAsync(AgentSpec spec, string workspace, string prompt, string artifactRoot,
        TimeSpan timeout, bool readOnly, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(artifactRoot);
        var adapter = new CodexAdapter();
        var invocation = adapter.Build(spec, WorkspaceMount, prompt, "/artifacts");
        var inner = invocation.Process.Arguments.ToList();
        var sandbox = inner.IndexOf("--sandbox");
        if (sandbox >= 0) { inner.RemoveAt(sandbox); inner.RemoveAt(sandbox); }
        inner.Insert(1, "--dangerously-bypass-approvals-and-sandbox");
        inner.Insert(1, "--ignore-rules");
        inner.Insert(1, "--ignore-user-config");
        var innerInvocation = invocation with { Process = invocation.Process with { FileName = "codex-isolated", Arguments = inner, WorkingDirectory = WorkspaceMount } };
        List<string> command = ["codex-isolated", .. inner];
        var result = await RunAgentContainerAsync(workspace, readOnly, command, timeout, prompt, cancellationToken);
        var raw = Path.Combine(artifactRoot, invocation.RawLogName);
        var stderr = Path.Combine(artifactRoot, invocation.StdErrName);
        File.WriteAllText(raw, result.Stdout); File.WriteAllText(stderr, result.Stderr);
        var versionResult = await RunAgentContainerAsync(workspace, true, ["codex-isolated", "--version"], TimeSpan.FromMinutes(1), null, cancellationToken);
        var version = versionResult.ExitCode == 0 ? (versionResult.Stdout + versionResult.Stderr).Trim() : null;
        var telemetry = adapter.Parse(spec, innerInvocation, result, version);
        Json.Write(Path.Combine(artifactRoot, "telemetry.json"), telemetry);
        return new(telemetry, result, telemetry.FinalResponse ?? result.Stdout);
    }

    private async Task<ProcessResult> RunAgentContainerAsync(string workspace, bool readOnly, IReadOnlyList<string> command,
        TimeSpan timeout, string? input, CancellationToken cancellationToken = default)
    {
        var name = NextName("agent");
        var args = CommonRunArguments(name);
        AddBind(args, workspace, WorkspaceMount, readOnly);
        args.AddRange(AgentVolumeMountArguments(nugetVolume));
        AddEnvironment(args);
        args.Add(options.AgentImage);
        args.AddRange(command);
        return await RunDockerAsync(name, args, timeout, input, cancellationToken);
    }

    private async Task<ProcessResult> RunEvaluatorAsync(string? workspace, string? artifacts, IReadOnlyList<string> command,
        TimeSpan timeout, string? baselinePath = null)
    {
        var name = NextName("evaluator");
        var args = CommonRunArguments(name);
        if (workspace is not null) AddBind(args, workspace, WorkspaceMount, readOnly: false);
        if (artifacts is not null) AddBind(args, artifacts, "/artifacts", readOnly: false);
        if (baselinePath is not null) AddBind(args, baselinePath, "/baseline.json", readOnly: true);
        args.AddRange(["--mount", $"type=volume,src={nugetVolume},dst=/nuget"]);
        AddEnvironment(args);
        args.Add(options.EvaluatorImage);
        args.AddRange(command);
        return await RunDockerAsync(name, args, timeout, null);
    }

    internal static List<string> CommonRunArguments(string name) =>
    [
        "run", "--rm", "--interactive", "--name", name, "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges", "--pids-limit", PidsLimit.ToString(CultureInfo.InvariantCulture),
        "--memory", MemoryLimit, "--cpus", CpuLimit.ToString(CultureInfo.InvariantCulture),
        "--network", Network, "--tmpfs", "/tmp:rw,nosuid,nodev,size=2g"
    ];

    private static void AddBind(List<string> args, string source, string destination, bool readOnly)
    {
        var value = $"type=bind,src={Path.GetFullPath(source)},dst={destination}" + (readOnly ? ",readonly" : "");
        args.Add("--mount"); args.Add(value);
    }

    private static void AddEnvironment(List<string> args) => args.AddRange([
        "--env", $"CODEX_HOME={CodexHome}", "--env", "HOME=/tmp/home", "--env", "DOTNET_CLI_HOME=/tmp/dotnet",
        "--env", "XDG_DATA_HOME=/tmp/home/.local/share", "--env", "XDG_CONFIG_HOME=/tmp/home/.config",
        "--env", "NUGET_PACKAGES=/nuget/packages", "--env", "DOTNET_NOLOGO=1", "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1"
    ]);

    internal static IReadOnlyList<string> AgentVolumeMountArguments(string nugetVolume) =>
    [
        "--mount", $"type=volume,src={AuthVolume},dst={CodexHome}",
        "--mount", $"type=volume,src={nugetVolume},dst=/nuget"
    ];

    private async Task<ProcessResult> RunDockerAsync(string name, IReadOnlyList<string> args, TimeSpan timeout, string? input,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result;
        try
        {
            result = await processes.RunAsync(new("docker", args, Environment.CurrentDirectory, input), timeout,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await processes.RunAsync(new("docker", ["rm", "-f", name], Environment.CurrentDirectory),
                    TimeSpan.FromMinutes(1), cancellationToken: CancellationToken.None);
            }
            catch { }
            throw;
        }
        if (result.TimedOut)
            try
            {
                await processes.RunAsync(new("docker", ["rm", "-f", name], Environment.CurrentDirectory),
                    TimeSpan.FromMinutes(1), cancellationToken: CancellationToken.None);
            }
            catch { }
        return result;
    }

    private async Task SeedAuthenticationAsync()
    {
        var name = NextName("auth-seed");
        var args = new List<string> { "run", "--rm", "--name", name, "--user", "0", "--network", "none" };
        args.AddRange(["--mount", $"type=volume,src={AuthVolume},dst={CodexHome}"]);
        AddBind(args, options.CodexAuthFile, "/seed/auth.json", readOnly: true);
        args.Add(options.AgentImage); args.Add("seed-codex-auth");
        var result = await RunDockerAsync(name, args, TimeSpan.FromMinutes(1), null);
        RequireSuccess(result, "seed the persistent Codex authentication volume");
    }

    private async Task VerifyAuthenticationAsync()
    {
        var name = NextName("auth-check");
        var args = CommonRunArguments(name);
        args.AddRange(["--mount", $"type=volume,src={AuthVolume},dst={CodexHome}",
            "--mount", $"type=volume,src={nugetVolume},dst=/nuget"]);
        AddEnvironment(args);
        args.Add(options.AgentImage);
        args.AddRange(["codex-isolated", "login", "status"]);
        var result = await RunDockerAsync(name, args, TimeSpan.FromMinutes(1), null);
        RequireSuccess(result, "verify Codex authentication inside the agent container");
    }

    private async Task CreateVolumeAsync(string volume)
    {
        var result = await processes.RunAsync(new("docker", ["volume", "create", volume], Environment.CurrentDirectory), TimeSpan.FromMinutes(1));
        RequireSuccess(result, $"create Docker volume {volume}");
    }

    private async Task<ProcessResult> Git(string directory, IReadOnlyList<string> arguments)
    {
        var result = await processes.RunAsync(new("git", arguments, directory), TimeSpan.FromMinutes(3));
        RequireSuccess(result, "prepare isolated Git repository");
        return result;
    }

    private static async Task<string> InspectImage(ProcessRunner processes, string image)
    {
        var result = await processes.RunAsync(new("docker", ["image", "inspect", "--format", "{{.Id}}", image], Environment.CurrentDirectory), TimeSpan.FromMinutes(1));
        if (result.ExitCode != 0) throw new HarnessException($"Docker image '{image}' is missing. Build the container targets first.");
        return result.Stdout.Trim();
    }

    private static void RequireSuccess(ProcessResult result, string action)
    {
        if (result.ExitCode != 0 || result.TimedOut)
            throw new HarnessException($"Could not {action} (exit {result.ExitCode}, timeout={result.TimedOut}): {result.Stderr.Trim()}");
    }

    private string NextName(string role) => $"todoapp-eval-{role}-{Environment.ProcessId}-{Interlocked.Increment(ref sequence)}";
    private static string Minutes(TimeSpan timeout) =>
        Math.Max(1, (int)Math.Ceiling(timeout.TotalMinutes)).ToString(CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        try { await processes.RunAsync(new("docker", ["volume", "rm", "-f", nugetVolume], Environment.CurrentDirectory), TimeSpan.FromMinutes(1)); }
        catch { }
    }
}

internal sealed class DockerCodexExecutor(DockerEvaluationRuntime runtime) : IAgentExecutor
{
    public Task<AgentExecution> ExecuteAsync(AgentSpec spec, string workspace, string prompt, string artifactRoot,
        TimeSpan timeout, bool readOnly, string? fakeScript = null, CancellationToken cancellationToken = default)
    {
        if (spec.Provider != CliProvider.Codex) throw new HarnessException("The Docker agent image supports Codex only.");
        return runtime.ExecuteCodexAsync(spec, workspace, prompt, artifactRoot, timeout, readOnly, cancellationToken);
    }
}
