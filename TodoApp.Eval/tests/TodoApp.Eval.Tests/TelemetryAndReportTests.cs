using System.Text.Json;

namespace TodoApp.Eval.Tests;

public sealed class TelemetryAndReportTests
{
    [Fact]
    public void Aggregation_sums_completed_roles_and_propagates_unknown_metrics()
    {
        var implementation = Telemetry("implementation", input: 100, output: 30, reasoning: 4, shells: 3);
        var grader = Telemetry("grader", input: 20, output: 10, reasoning: null, shells: null);
        var snapshot = RunTelemetry.Create(implementation).WithSemanticGrader(grader);

        Assert.Equal(2, snapshot.Aggregate!.Roles);
        Assert.Equal(120, snapshot.Aggregate.Tokens.Input);
        Assert.Equal(40, snapshot.Aggregate.Tokens.Output);
        Assert.Equal(160, snapshot.Aggregate.Tokens.InputOutput);
        Assert.Null(snapshot.Aggregate.Tokens.Reasoning);
        Assert.Null(snapshot.Aggregate.ShellCommands);
    }

    [Fact]
    public void Partial_snapshot_preserves_implementation_agent_and_aggregate()
    {
        var root = Path.Combine(Path.GetTempPath(), "todo-eval-telemetry-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "telemetry.json");
        try
        {
            Json.Write(path, RunTelemetry.Create(Telemetry("implementation")));
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            Assert.True(document.RootElement.TryGetProperty("agent", out _));
            Assert.False(document.RootElement.TryGetProperty("semanticGrader", out _));
            Assert.Equal(1, document.RootElement.GetProperty("aggregate").GetProperty("roles").GetInt32());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Report_contains_single_grader_telemetry_profile_budget_and_lost_point_reason()
    {
        var root = Path.Combine(Path.GetTempPath(), "todo-eval-report-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "report.md");
        Directory.CreateDirectory(root);
        try
        {
            var implementation = Telemetry("implementation", reportedModel: null, input: 100, cached: 20, output: 30, reasoning: 4, shells: 3);
            var graderTelemetry = Telemetry("gpt-5.6-terra", effort: "high", input: 20, cached: 5, output: 10, reasoning: 2, shells: 1);
            var runTelemetry = RunTelemetry.Create(implementation).WithSemanticGrader(graderTelemetry);
            var implementationSpec = new AgentSpec(CliProvider.Codex, "implementation", ReasoningEffort: "high");
            var graderSpec = new AgentSpec(CliProvider.Codex, "gpt-5.6-terra", ReasoningEffort: "high");
            var now = DateTimeOffset.UtcNow;
            var health = GraderHealth.Assess(graderTelemetry, GraderBudget.AgenticV2);
            var manifest = new RunManifest("run-1", "/repo", "base", "resolved", "/worktree", root, "9.0.305",
                implementationSpec, graderSpec, "agentic-v2", "profile-hash", GraderBudget.AgenticV2,
                now, now, false, SemanticGraderTelemetry: graderTelemetry, GraderHealth: health);
            var criterion = new SemanticCriterionGrade(
                "functional.contract-completeness", "functional", "Functional behavior", "Contract completeness", 20,
                SemanticLevel.Strong, SemanticLevel.AdequateWithGaps, 10,
                "Unicode case folding remains incomplete.", [new(EvidenceKind.Source, "source", "source.cs", 1)], ["F-1"],
                "Implement unrestricted case-insensitive matching.");
            var finding = new SemanticFinding("F-1", criterion.CriterionId, SemanticFindingSeverity.Medium,
                "A non-ASCII title is searched with different casing.", "The matching todo is omitted.",
                "source.cs", 1, [new(EvidenceKind.Source, "source", "source.cs", 1)]);
            var semantic = new SemanticGrade("agentic-v2", "profile-hash", 90, 100, 27, 30,
                [new("functional", "Functional behavior", 30, 20)], [criterion],
                [new("requiredBehaviorComplete", false, "functional.contract-completeness is below strong."),
                 new("noCriticalSemanticFinding", true, "No high-severity finding.")],
                [finding], new(["source.cs"], ["tests"]), "good with one gap", ["direct"], ["unicode gap"], ["add Unicode test"]);
            var deterministic = Deterministic();
            var grade = Scoring.Calculate(deterministic, semantic, true);

            ReportWriter.Write(path, manifest, runTelemetry, deterministic, semantic, grade);
            var report = File.ReadAllText(path);

            Assert.Contains("semantic 27/30", report);
            Assert.Contains("Semantic quality:** 90.0/100", report);
            Assert.Contains("| Implementation | implementation | n/a | high | codex 1.0 |", report);
            Assert.Contains("| Semantic grader | gpt-5.6-terra | reported-gpt-5.6-terra | high | codex 1.0 |", report);
            Assert.DoesNotContain("Overall grader", report);
            Assert.Contains("| Total | n/a | n/a | n/a | n/a |", report);
            Assert.Contains("| 120 | 25 | 40 | 6 | 160 |", report);
            Assert.Contains("`agentic-v2` / `profile-hash`", report);
            Assert.Contains("300s, 8 tool calls, 2 focused test commands", report);
            Assert.Contains("Unicode case folding remains incomplete.", report);
            Assert.Contains("Implement unrestricted case-insensitive matching.", report);
            Assert.Contains("requiredBehaviorComplete", report);
            Assert.Contains("functional.contract-completeness is below strong.", report);
            Assert.Contains("unknown—not zero", report);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Grader_budget_overrun_is_a_health_warning_only()
    {
        var telemetry = Telemetry("grader", tools: 9, tests: 3);

        var health = GraderHealth.Assess(telemetry, GraderBudget.AgenticV1);

        Assert.False(health.WithinBudget);
        Assert.Equal(2, health.Warnings.Count);
    }

    private static AgentTelemetry Telemetry(string model, string effort = "high", string? reportedModel = null,
        long? input = 10, long? cached = 2, long? output = 5, long? reasoning = 1, int? shells = 1,
        int? tools = 2, int? tests = 1) => new(
        "codex", model, effort, reportedModel ?? (model == "implementation" ? null : "reported-" + model), "codex 1.0",
        ["codex", "exec"], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(2), 2, 0, false, 10, 0,
        new(input, cached, output, reasoning), 1, tools, shells, 0, 0, 1, tests, 0, false, false, null, "done", false);

    private static DeterministicResult Deterministic()
    {
        var gates = new Dictionary<string, bool>
        {
            ["build"] = true,
            ["existingTests"] = true,
            ["format"] = true,
            ["noNewAnalyzerDiagnostics"] = true,
            ["noNewFormatViolations"] = true,
            ["submittedTests"] = true,
            ["noNewVulnerabilities"] = true,
            ["noPackageOrBuildChanges"] = true,
            ["noMigrations"] = true,
            ["noBinaries"] = true,
            ["noSecrets"] = true,
            ["diffCheck"] = true,
            ["privateAcceptanceTests"] = true
        };
        var acceptance = new Dictionary<string, bool>
        {
            ["authenticationIsolation"] = true,
            ["filtering"] = true,
            ["paginationContract"] = true,
            ["validationRegression"] = true
        };
        return new([], [], [], [], new(1, 0, 0, 1, true), new(1, 0, 0, 1, true), new(80, 70),
            new([], 0, 0, 1, 1, 0, [], [], [], [], [], [], true, true, true), acceptance, gates, true);
    }
}
