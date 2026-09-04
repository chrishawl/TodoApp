using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoApp.Eval;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Could not deserialize {path}.");
}

internal enum CliProvider { Codex, Claude, Copilot, Fake }

internal enum IsolationKind { Host, Container }

internal sealed record AgentSpec(
    CliProvider Provider,
    string Model,
    string? Executable = null,
    string? ReasoningEffort = null);

internal sealed record EvalOptions(
    string Repository,
    string BaseCommit,
    AgentSpec Implementation,
    AgentSpec SemanticGrader,
    string OutputRoot,
    string WorktreeRoot,
    TimeSpan Timeout,
    bool Cleanup,
    string? RunId = null,
    string? FakeScript = null,
    IsolationKind Isolation = IsolationKind.Container,
    string AgentImage = "todoapp-eval-agent:local",
    string EvaluatorImage = "todoapp-eval-evaluator:local",
    string CodexAuthFile = "",
    string ReviewProfile = "agentic-v2");

internal sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? StandardInput = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

internal sealed record ProcessResult(
    int ExitCode,
    bool TimedOut,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long StdoutBytes,
    long StderrBytes,
    string Stdout,
    string Stderr)
{
    public TimeSpan Duration => FinishedAt - StartedAt;
}

internal sealed record TokenUsage(long? Input, long? CachedInput, long? Output, long? Reasoning)
{
    public long? InputOutput => Input is null || Output is null ? null : Input + Output;
}

internal sealed record AgentTelemetry(
    string Provider,
    string RequestedModel,
    string? ConfiguredReasoningEffort,
    string? ReportedModel,
    string? CliVersion,
    IReadOnlyList<string> Command,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    double WallClockSeconds,
    int ExitCode,
    bool TimedOut,
    long StdoutBytes,
    long StderrBytes,
    TokenUsage Tokens,
    int? Turns,
    int? ToolCalls,
    int? ShellCommands,
    int? FailedTools,
    int? RepeatedCommands,
    int? BuildInvocations,
    int? TestInvocations,
    int? RecoveryAttempts,
    bool? HostSkillsAvailable,
    bool? ProviderUsageExposed,
    IReadOnlyList<string>? ObservedSkillNames,
    string? FinalResponse,
    bool? ClaimedUnobservedTests,
    IReadOnlyList<string>? ObservedShellCommands = null)
{
    public string ConfiguredModel => RequestedModel;
}

internal sealed record AggregateAgentTelemetry(
    int Roles,
    double WallClockSeconds,
    TokenUsage Tokens,
    int? Turns,
    int? ToolCalls,
    int? ShellCommands,
    int? FailedTools,
    int? RepeatedCommands,
    int? BuildInvocations,
    int? TestInvocations,
    int? RecoveryAttempts);

internal sealed record RunTelemetry(
    AgentTelemetry Agent,
    AgentTelemetry? SemanticGrader = null,
    AggregateAgentTelemetry? Aggregate = null,
    int? FilesChanged = null,
    int? TotalChurn = null,
    double? ProductionToTestChurnRatio = null,
    bool? HasSubmittedTests = null,
    bool? SubmittedTestsDiscovered = null,
    bool? FinalResponseClaimedUnobservedTests = null)
{
    public static RunTelemetry Create(AgentTelemetry implementation) =>
        new(implementation, Aggregate: AgentTelemetryAggregation.Sum([implementation]),
            ProductionToTestChurnRatio: null,
            FinalResponseClaimedUnobservedTests: implementation.ClaimedUnobservedTests);

    public RunTelemetry WithSemanticGrader(AgentTelemetry telemetry) =>
        this with { SemanticGrader = telemetry, Aggregate = AgentTelemetryAggregation.Sum([Agent, telemetry]) };
}

internal static class AgentTelemetryAggregation
{
    public static AggregateAgentTelemetry Sum(IReadOnlyList<AgentTelemetry> roles) => new(
        roles.Count,
        roles.Sum(x => x.WallClockSeconds),
        new(
            SumNullable(roles, x => x.Tokens.Input),
            SumNullable(roles, x => x.Tokens.CachedInput),
            SumNullable(roles, x => x.Tokens.Output),
            SumNullable(roles, x => x.Tokens.Reasoning)),
        SumNullable(roles, x => x.Turns),
        SumNullable(roles, x => x.ToolCalls),
        SumNullable(roles, x => x.ShellCommands),
        SumNullable(roles, x => x.FailedTools),
        SumNullable(roles, x => x.RepeatedCommands),
        SumNullable(roles, x => x.BuildInvocations),
        SumNullable(roles, x => x.TestInvocations),
        SumNullable(roles, x => x.RecoveryAttempts));

    private static long? SumNullable(IReadOnlyList<AgentTelemetry> roles, Func<AgentTelemetry, long?> select)
    {
        var values = roles.Select(select).ToArray();
        return values.Any(x => x is null) ? null : values.Sum(x => x!.Value);
    }

    private static int? SumNullable(IReadOnlyList<AgentTelemetry> roles, Func<AgentTelemetry, int?> select)
    {
        var values = roles.Select(select).ToArray();
        return values.Any(x => x is null) ? null : values.Sum(x => x!.Value);
    }
}

internal sealed record DiagnosticFinding(string Severity, string Rule, string File, string Message);
internal sealed record FormatFinding(string File, string Diagnostic, string Message);
internal sealed record VulnerabilityFinding(string Package, string Version, string Advisory, string Severity);
internal sealed record TestSummary(int Passed, int Failed, int Skipped, double DurationSeconds, bool Discovered);
internal sealed record CoverageSummary(double? LinePercent, double? BranchPercent);

internal sealed record DiffInventory(
    IReadOnlyList<string> ChangedFiles,
    int AddedLines,
    int DeletedLines,
    int ProductionLines,
    int TestLines,
    int Commits,
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<string> BinaryFiles,
    IReadOnlyList<string> Symlinks,
    IReadOnlyList<string> PackageOrProjectFiles,
    IReadOnlyList<string> Migrations,
    IReadOnlyList<string> SecretFindings,
    bool DiffCheckPassed,
    bool HasProductionPatch,
    bool HasSubmittedTests);

internal sealed record CheckResult(string Name, bool Passed, int ExitCode, bool TimedOut, double DurationSeconds, string Artifact);

internal sealed record BaselineReadiness(
    bool Ready,
    string ResolvedCommit,
    string DotnetSdk,
    bool RestorePassed,
    bool BuildPassed,
    bool ExistingTestsPassed,
    bool FormatPassed,
    bool WorktreeClean,
    int ExpectedTestCount,
    TestSummary ExistingTests,
    IReadOnlyList<string> WorktreeChanges,
    IReadOnlyList<string> Failures);

internal sealed record DeterministicResult(
    IReadOnlyList<CheckResult> Checks,
    IReadOnlyList<DiagnosticFinding> AnalyzerFindings,
    IReadOnlyList<FormatFinding> FormatFindings,
    IReadOnlyList<VulnerabilityFinding> Vulnerabilities,
    TestSummary ExistingTests,
    TestSummary PrivateTests,
    CoverageSummary Coverage,
    DiffInventory Diff,
    IReadOnlyDictionary<string, bool> AcceptanceGroups,
    IReadOnlyDictionary<string, bool> HardGates,
    bool Passed);

internal sealed record GradeResult(
    string Status,
    int Score,
    int AcceptancePoints,
    int EngineeringPoints,
    int SemanticPoints,
    IReadOnlyDictionary<string, bool> HardGates,
    string Narrative,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Recommendations);

internal sealed record GraderCostInputs(
    int ModelCalls,
    string Provider,
    string Model,
    string? ReasoningEffort,
    TokenUsage Tokens);

internal sealed record RunManifest(
    string RunId,
    string Repository,
    string BaseCommit,
    string ResolvedCommit,
    string Worktree,
    string ResultDirectory,
    string DotnetSdk,
    AgentSpec Implementation,
    AgentSpec SemanticGrader,
    string RubricProfileId,
    string RubricProfileHash,
    GraderBudget GraderBudget,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    bool Cleanup,
    BaselineReadiness? BaselineReadiness = null,
    IsolationKind Isolation = IsolationKind.Host,
    ContainerEnvironment? Container = null,
    AgentTelemetry? SemanticGraderTelemetry = null,
    GraderHealth? GraderHealth = null,
    GraderCostInputs? GraderCostInputs = null,
    string HarnessVersion = "4.0.0",
    string? TaskHash = null);

internal sealed record ContainerEnvironment(
    string AgentImage,
    string AgentImageId,
    string EvaluatorImage,
    string EvaluatorImageId,
    string AuthVolume,
    string NuGetVolume,
    string WorkspaceMount,
    string CodexHome,
    string Network,
    string MemoryLimit,
    double CpuLimit,
    int PidsLimit,
    bool ReadOnlyRoot,
    bool CapabilitiesDropped,
    bool NoNewPrivileges,
    bool UserConfigIgnored,
    bool SessionEphemeral);

internal sealed class HarnessException(string message, Exception? inner = null) : Exception(message, inner);
