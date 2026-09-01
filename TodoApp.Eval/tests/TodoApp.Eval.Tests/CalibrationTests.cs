namespace TodoApp.Eval.Tests;

public sealed class CalibrationTests
{
    [Fact]
    public void Agentic_pack_has_nine_valid_known_answer_cases()
    {
        var profile = SemanticTestData.Profile();
        var pack = CalibrationPack.Load(Path.Combine(AppContext.BaseDirectory, "calibration", "agentic-v1", "cases.json"));

        pack.ValidateAgainst(profile);

        Assert.Equal(9, pack.Cases.Count);
        Assert.Equal(new("gpt-5.6-terra", "high"), pack.SelectedConfiguration);
        Assert.Equal(new("gpt-5.6-sol", "high"), pack.ComparisonConfiguration);
        Assert.All(pack.Cases, item => Assert.False(string.IsNullOrWhiteSpace(item.Fixture)));
    }

    [Fact]
    public void Verifier_accepts_required_finding_and_criterion_ceiling()
    {
        const string criterionId = "security.data-protection";
        var pack = Pack(new(
            "leak", "fixture", [criterionId], [],
            new Dictionary<string, SemanticVerdict> { [criterionId] = SemanticVerdict.NotMet },
            new Dictionary<string, SemanticVerdict>()));
        var finding = SemanticTestData.Finding("F-1", criterionId, SemanticFindingSeverity.High);
        var criterion = new SemanticCriterionGrade(criterionId, "security", SemanticVerdict.NotMet, 0, "leak", [], ["F-1"]);
        var grade = Grade(criterion, [finding]);

        var result = CalibrationVerifier.Verify(pack, new Dictionary<string, SemanticGrade> { ["leak"] = grade });

        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Verifier_reports_misses_false_positives_and_bound_violations()
    {
        const string missed = "security.data-protection";
        const string falsePositive = "performance.efficiency";
        var pack = Pack(new(
            "bad", "fixture", [missed], [falsePositive],
            new Dictionary<string, SemanticVerdict> { [missed] = SemanticVerdict.Partial },
            new Dictionary<string, SemanticVerdict>()));
        var finding = SemanticTestData.Finding("F-1", falsePositive, SemanticFindingSeverity.Low);
        var criteria = new[]
        {
            new SemanticCriterionGrade(missed, "security", SemanticVerdict.Met, 2, "wrong", [], []),
            new SemanticCriterionGrade(falsePositive, "performance", SemanticVerdict.Partial, 1, "wrong", [], ["F-1"])
        };

        var result = CalibrationVerifier.Verify(pack,
            new Dictionary<string, SemanticGrade> { ["bad"] = Grade(criteria, [finding]) });

        Assert.False(result.Passed);
        Assert.Equal(3, result.Failures.Count);
    }

    [Fact]
    public void Retained_run_verification_checks_manifest_model_and_telemetry()
    {
        const string criterionId = "security.data-protection";
        using var workspace = SemanticTestData.Workspace();
        var resultDirectory = Path.Combine(workspace.Path, "case");
        Directory.CreateDirectory(resultDirectory);
        var item = new CalibrationCase(
            "case", "fixture", [], [], new Dictionary<string, SemanticVerdict>(), new Dictionary<string, SemanticVerdict>());
        var pack = Pack(item);
        Json.Write(Path.Combine(resultDirectory, "semantic-grade.json"),
            Grade(new SemanticCriterionGrade(criterionId, "security", SemanticVerdict.Met, 2, "sound", [], []), []));
        var configuration = pack.SelectedConfiguration;
        var grader = new AgentSpec(CliProvider.Codex, configuration.Model, ReasoningEffort: configuration.ReasoningEffort);
        var telemetry = SemanticTestData.Telemetry(configuration.Model, configuration.ReasoningEffort);
        var now = DateTimeOffset.UtcNow;
        Json.Write(Path.Combine(resultDirectory, "manifest.json"), new RunManifest(
            "case", "/repo", "base", "resolved", "/worktree", resultDirectory, "9.0.305",
            grader, grader, "agentic-v1", "hash", GraderBudget.AgenticV1, now, now, false,
            SemanticGraderTelemetry: telemetry));

        var result = CalibrationVerifier.VerifyRun(pack, workspace.Path, configuration);

        Assert.True(result.Passed);
    }

    private static CalibrationPack Pack(CalibrationCase item) => new(
        "pack", "agentic-v1", new("gpt-5.6-terra", "high"), new("gpt-5.6-sol", "high"), [item]);

    private static SemanticGrade Grade(SemanticCriterionGrade criterion, IReadOnlyList<SemanticFinding> findings) =>
        Grade([criterion], findings);

    private static SemanticGrade Grade(IReadOnlyList<SemanticCriterionGrade> criteria, IReadOnlyList<SemanticFinding> findings) => new(
        "agentic-v1", "hash", 0, 30, 0, 30, criteria, findings, new(["source.cs"], []), "summary", [], [], []);
}
