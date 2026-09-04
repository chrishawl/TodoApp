using System.Text.Json;

namespace TodoApp.Eval.Tests;

public sealed class SemanticGradingTests
{
    [Fact]
    public void Agentic_v2_profile_owns_exact_weights_levels_anchors_and_generated_contract()
    {
        var profile = SemanticTestData.Profile();

        Assert.Equal("agentic-v2", profile.Rubric.Id);
        Assert.Equal(100, profile.Rubric.SemanticQualityPoints);
        Assert.Equal(30, profile.Rubric.CompositePoints);
        Assert.Equal(100, profile.Rubric.Dimensions.Sum(x => x.Weight));
        Assert.Equal(19, profile.Rubric.Criteria.Count);
        Assert.Equal(5, profile.Rubric.Levels.Count);
        Assert.All(profile.Rubric.Criteria, criterion => Assert.Equal(5, criterion.Anchors.Count));
        Assert.Equal(64, profile.Hash.Length);
        Assert.DoesNotContain("Todo", profile.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", profile.RubricJson, StringComparison.OrdinalIgnoreCase);

        using var schema = JsonDocument.Parse(profile.OutputSchema);
        var criterionEnum = schema.RootElement.GetProperty("$defs").GetProperty("criterionAssessment")
            .GetProperty("properties").GetProperty("criterionId").GetProperty("enum")
            .EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(profile.Rubric.Criteria.Select(x => x.Id), criterionEnum);
    }

    [Fact]
    public void Profile_validation_rejects_bad_weight_total_and_missing_anchor()
    {
        var profile = SemanticTestData.Profile().Rubric;
        var firstDimension = profile.Dimensions[0];
        var badWeight = profile with { Dimensions = [firstDimension with { Weight = firstDimension.Weight + 1 }, .. profile.Dimensions.Skip(1)] };
        Assert.Throws<HarnessException>(() => SemanticScoring.ValidateProfile(badWeight));

        var firstCriterion = firstDimension.Criteria[0];
        var badCriterion = firstCriterion with { Anchors = firstCriterion.Anchors.Skip(1).ToArray() };
        var badAnchors = profile with
        {
            Dimensions = [firstDimension with { Criteria = [badCriterion, .. firstDimension.Criteria.Skip(1)] }, .. profile.Dimensions.Skip(1)]
        };
        Assert.Throws<HarnessException>(() => SemanticScoring.ValidateProfile(badAnchors));
    }

    [Fact]
    public void Missing_and_duplicate_criteria_are_rejected()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var candidate = SemanticTestData.Candidate(workspace.Path, profile);
        var input = SemanticTestData.Input(profile);

        Assert.Contains("omitted criteria", Assert.Throws<HarnessException>(() =>
            SemanticScoring.Evaluate(profile, candidate, input with { Criteria = input.Criteria.Skip(1).ToArray() })).Message);
        Assert.Contains("duplicate criterion", Assert.Throws<HarnessException>(() =>
            SemanticScoring.Evaluate(profile, candidate, input with { Criteria = [.. input.Criteria, input.Criteria[0]] })).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_non_exemplary_level_requires_a_next_level_gap()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var input = SemanticTestData.Input(profile, _ => SemanticLevel.Strong);
        var first = input.Criteria[0] with { NextLevelGap = null };

        var error = Assert.Throws<HarnessException>(() => SemanticScoring.Evaluate(
            profile, SemanticTestData.Candidate(workspace.Path, profile), input with { Criteria = [first, .. input.Criteria.Skip(1)] }));

        Assert.Contains("nextLevelGap", error.Message);
    }

    [Fact]
    public void Evidence_outside_frozen_worktree_and_unobserved_commands_are_rejected()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var candidate = SemanticTestData.Candidate(workspace.Path, profile);
        var input = SemanticTestData.Input(profile);
        var escaped = input.Criteria[0] with { Evidence = [new(EvidenceKind.Source, "escaped", "../outside.cs", 1)] };
        Assert.Contains("escapes", Assert.Throws<HarnessException>(() => SemanticScoring.Evaluate(
            profile, candidate, input with { Criteria = [escaped, .. input.Criteria.Skip(1)] })).Message);

        const string command = "dotnet test --no-build --filter Search";
        var commandAssessment = input.Criteria[0] with
        {
            Evidence = [new(EvidenceKind.Command, "focused", Command: command)]
        };
        var commandInput = input with { Criteria = [commandAssessment, .. input.Criteria.Skip(1)] };
        Assert.Throws<HarnessException>(() => SemanticScoring.Evaluate(profile, candidate, commandInput));
        Assert.Equal(100, SemanticScoring.Evaluate(profile, candidate, commandInput, [command]).QualityScore);
    }

    [Fact]
    public void Weighted_arithmetic_and_composite_rounding_are_evaluator_owned()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var grade = SemanticScoring.Evaluate(
            profile,
            SemanticTestData.Candidate(workspace.Path, profile),
            SemanticTestData.Input(profile, _ => SemanticLevel.Strong));

        Assert.Equal(75m, grade.QualityScore);
        Assert.Equal(23, grade.CompositePoints);
        Assert.All(grade.Dimensions, dimension => Assert.Equal(dimension.Weight * 3m / 4m, dimension.EarnedPoints));
        Assert.All(grade.Criteria, criterion => Assert.Equal(criterion.Weight * 3m / 4m, criterion.EarnedPoints));
    }

    [Fact]
    public void High_medium_and_low_findings_apply_levels_zero_two_and_three_caps()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var ids = profile.Rubric.Criteria.Take(3).Select(x => x.Id).ToArray();
        var findings = new[]
        {
            SemanticTestData.Finding("F-high", ids[0], SemanticFindingSeverity.High),
            SemanticTestData.Finding("F-medium", ids[1], SemanticFindingSeverity.Medium),
            SemanticTestData.Finding("F-low", ids[2], SemanticFindingSeverity.Low)
        };

        var grade = SemanticScoring.Evaluate(profile, SemanticTestData.Candidate(workspace.Path, profile),
            SemanticTestData.Input(profile, _ => SemanticLevel.Exemplary, findings));

        Assert.Equal(SemanticLevel.Failed, grade.Criteria[0].Level);
        Assert.Equal(SemanticLevel.AdequateWithGaps, grade.Criteria[1].Level);
        Assert.Equal(SemanticLevel.Strong, grade.Criteria[2].Level);
        Assert.Equal(0, grade.Criteria[0].EarnedPoints);
        Assert.Equal(5, grade.Criteria[1].EarnedPoints);
        Assert.Equal(4.5m, grade.Criteria[2].EarnedPoints);
    }

    [Fact]
    public void Semantic_gates_require_both_functional_levels_and_no_high_finding()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var functional = profile.Rubric.RequiredBehaviorCriterionIds;
        var findings = new[]
        {
            SemanticTestData.Finding("F-material", functional[0], SemanticFindingSeverity.Medium),
            SemanticTestData.Finding("F-critical", "operations.security", SemanticFindingSeverity.High)
        };

        var grade = SemanticScoring.Evaluate(profile, SemanticTestData.Candidate(workspace.Path, profile),
            SemanticTestData.Input(profile, _ => SemanticLevel.Strong, findings));

        Assert.False(grade.Gates.Single(x => x.Id == "requiredBehaviorComplete").Passed);
        Assert.Contains("functional.contract-completeness", grade.Gates[0].Reason);
        Assert.False(grade.Gates.Single(x => x.Id == "noCriticalSemanticFinding").Passed);
        Assert.Contains("F-critical", grade.Gates[1].Reason);
    }

    [Fact]
    public void Unicode_contract_defect_fails_required_behavior_even_when_mechanical_evidence_passes()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var finding = SemanticTestData.Finding(
            "F-unicode", "functional.contract-completeness", SemanticFindingSeverity.Medium);
        var candidate = SemanticTestData.Candidate(workspace.Path, profile);
        Assert.True(candidate.MechanicalEvidence.BuildPassed);
        Assert.True(candidate.MechanicalEvidence.ExistingTests.Failed == 0);

        var grade = SemanticScoring.Evaluate(profile, candidate,
            SemanticTestData.Input(profile, _ => SemanticLevel.Strong, [finding]));

        Assert.Equal(SemanticLevel.AdequateWithGaps,
            grade.Criteria.Single(x => x.CriterionId == finding.CriterionId).Level);
        Assert.False(grade.Gates.Single(x => x.Id == "requiredBehaviorComplete").Passed);
    }

    [Fact]
    public async Task One_agent_call_returns_scored_grade_and_publishes_telemetry()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var telemetry = SemanticTestData.Telemetry("gpt-5.6-terra", "high");
        var executor = new StubAgentExecutor(JsonSerializer.Serialize(SemanticTestData.Input(profile), Json.Options), telemetry);
        var artifacts = Path.Combine(workspace.Path, "artifacts");
        AgentTelemetry? observed = null;
        var grader = new SemanticGrading(executor,
            new(CliProvider.Codex, "gpt-5.6-terra", ReasoningEffort: "high"),
            profile, artifacts, GraderBudget.AgenticV2, value => observed = value);

        var grade = await grader.GradeAsync(SemanticTestData.Candidate(workspace.Path, profile), CancellationToken.None);

        Assert.Equal(1, executor.Calls);
        Assert.Same(telemetry, observed);
        Assert.Equal(100, grade.QualityScore);
        Assert.True(File.Exists(Path.Combine(artifacts, "semantic-grade.json")));
        Assert.DoesNotContain("privateTests", executor.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Path, executor.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"patch\"", executor.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompt_and_schema_share_profile_owned_evidence_and_level_contracts()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var executor = new StubAgentExecutor(JsonSerializer.Serialize(SemanticTestData.Input(profile), Json.Options),
            SemanticTestData.Telemetry("gpt-5.6-terra", "high"));
        var grader = new SemanticGrading(executor,
            new(CliProvider.Codex, "gpt-5.6-terra", ReasoningEffort: "high"), profile,
            Path.Combine(workspace.Path, "artifacts"), GraderBudget.AgenticV2);

        await grader.GradeAsync(SemanticTestData.Candidate(workspace.Path, profile), CancellationToken.None);

        foreach (var signal in profile.Rubric.EvidenceRequirements.MechanicalSignals)
            Assert.Contains($"`{signal}`", executor.Prompt, StringComparison.Ordinal);
        Assert.Contains("Every level below exemplary requires", executor.Prompt, StringComparison.Ordinal);
        Assert.Contains("nextLevelGap", profile.OutputSchema, StringComparison.Ordinal);
        Assert.Contains("adequateWithGaps", profile.OutputSchema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_owned_totals_and_numeric_levels_are_rejected()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var valid = JsonSerializer.Serialize(SemanticTestData.Input(profile), Json.Options);
        var withTotal = valid.Insert(valid.LastIndexOf('}'), ",\"total\":100");
        var totalGrader = Grader(workspace.Path, profile, withTotal);
        Assert.Contains("compiled profile contract", (await Assert.ThrowsAsync<HarnessException>(() =>
            totalGrader.GradeAsync(SemanticTestData.Candidate(workspace.Path, profile), CancellationToken.None))).Message);

        var numeric = valid.Replace("\"level\": \"exemplary\"", "\"level\": 4", StringComparison.Ordinal);
        await Assert.ThrowsAsync<HarnessException>(() => Grader(workspace.Path, profile, numeric)
            .GradeAsync(SemanticTestData.Candidate(workspace.Path, profile), CancellationToken.None));
    }

    [Fact]
    public void Agentic_v1_profile_and_historical_grade_remain_readable()
    {
        var profile = ReviewProfile.Load(AppContext.BaseDirectory, "agentic-v1");
        Assert.True(profile.IsLegacy);
        Assert.Equal(15, profile.Rubric.Criteria.Count);

        using var workspace = SemanticTestData.Workspace();
        var path = Path.Combine(workspace.Path, "semantic-grade.json");
        File.WriteAllText(path, """
            {
              "rubricProfileId":"agentic-v1","rubricProfileHash":"hash","score":23,"maximumScore":30,
              "rawPoints":23,"applicableRawPoints":30,
              "criteria":[{"criterionId":"one","attribute":"legacy","verdict":"partial","points":1,"rationale":"gap","evidence":[],"findingIds":[]}],
              "findings":[],"coverageReceipt":{"filesInspected":["source.cs"],"pathsInspected":[]},
              "summary":"legacy","strengths":[],"failures":[],"recommendations":[]
            }
            """);

        var grade = SemanticGradePersistence.Read(path);

        Assert.Equal(23, grade.CompositePoints);
        Assert.Equal(SemanticLevel.AdequateWithGaps, grade.Criteria[0].Level);
    }

    private static SemanticGrading Grader(string workspace, ReviewProfile profile, string response) => new(
        new StubAgentExecutor(response, SemanticTestData.Telemetry("gpt-5.6-terra", "high")),
        new(CliProvider.Codex, "gpt-5.6-terra", ReasoningEffort: "high"),
        profile, Path.Combine(workspace, "artifacts-" + Guid.NewGuid().ToString("N")), GraderBudget.AgenticV2);

    private sealed class StubAgentExecutor(string response, AgentTelemetry telemetry) : IAgentExecutor
    {
        public int Calls { get; private set; }
        public string Prompt { get; private set; } = "";

        public Task<AgentExecution> ExecuteAsync(AgentSpec spec, string workspace, string prompt, string artifactRoot,
            TimeSpan timeout, bool readOnly, string? fakeScript = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Prompt = prompt;
            var process = new ProcessResult(0, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, 0, response, "");
            return Task.FromResult(new AgentExecution(telemetry, process, response));
        }
    }
}

internal static class SemanticTestData
{
    public static ReviewProfile Profile() => ReviewProfile.Load(AppContext.BaseDirectory, "agentic-v2");

    public static TemporaryWorkspace Workspace()
    {
        var workspace = new TemporaryWorkspace();
        File.WriteAllText(Path.Combine(workspace.Path, "source.cs"), "// source\n");
        return workspace;
    }

    public static GradingCase Candidate(string worktree, ReviewProfile profile, IReadOnlyList<string>? changedFiles = null) => new(
        "task", "architecture", "diff --git a/source.cs b/source.cs", worktree, "base", new string('a', 64),
        changedFiles ?? ["source.cs"],
        new(true, true, new(1, 0, 0, 1, true), true, true, true, true, new(80, 70),
            new(changedFiles ?? ["source.cs"], 1, 0, true, true, true, true, true, true, true)),
        profile.Rubric.Id, profile.Hash);

    public static SemanticGradeInput Input(
        ReviewProfile profile,
        Func<string, SemanticLevel>? level = null,
        IReadOnlyList<SemanticFinding>? findings = null)
    {
        level ??= _ => SemanticLevel.Exemplary;
        findings ??= [];
        var findingIds = findings.GroupBy(x => x.CriterionId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Select(y => y.Id).ToArray(), StringComparer.Ordinal);
        var criteria = profile.Rubric.Criteria.Select(criterion =>
        {
            var value = level(criterion.Id);
            return new CriterionAssessment(
                criterion.Id, value, "Traceable rationale.",
                [new(EvidenceKind.Source, "Source establishes the criterion.", "source.cs", 1)],
                findingIds.GetValueOrDefault(criterion.Id) ?? [],
                value == SemanticLevel.Exemplary ? null : "Provide affirmative evidence for the next anchor.");
        }).ToArray();
        return new(criteria, findings, new(["source.cs"], []), "Evidence-backed summary.", ["strength"], [], ["recommendation"]);
    }

    public static SemanticFinding Finding(string id, string criterionId, SemanticFindingSeverity severity) => new(
        id, criterionId, severity, "A realistic trigger occurs.", "Observable behavior is incorrect.",
        "source.cs", 1, [new(EvidenceKind.Source, "The source shows the behavior.", "source.cs", 1)]);

    public static SemanticGrade Grade(int compositePoints = 30, decimal? qualityScore = null, bool gatesPass = true)
    {
        var quality = qualityScore ?? compositePoints * 100m / 30m;
        return new(
            "agentic-v2", "hash", quality, 100, compositePoints, 30,
            [new("functional", "Functional behavior", 30, quality * 0.3m)], [],
            [new("requiredBehaviorComplete", gatesPass, gatesPass ? "complete" : "incomplete"),
             new("noCriticalSemanticFinding", gatesPass, gatesPass ? "clear" : "critical")],
            [], new(["source.cs"], []), "summary", ["strength"], [], ["recommendation"]);
    }

    public static SemanticCriterionGrade Criterion(
        string id,
        string dimension,
        int weight,
        SemanticLevel level,
        IReadOnlyList<string>? findingIds = null) => new(
        id, dimension, dimension, id, weight, level, level, weight * (int)level / 4m,
        "rationale", [], findingIds ?? [], level == SemanticLevel.Exemplary ? null : "next");

    public static AgentTelemetry Telemetry(string model, string effort) => new(
        "codex", model, effort, model, "codex 1.0", ["codex", "exec"],
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(2), 2, 0, false, 10, 0,
        new(10, 2, 5, 1), 1, 2, 1, 0, 0, 0, 1, 0, false, false, null, "done", false);
}

internal sealed class TemporaryWorkspace : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "todo-eval-semantic-" + Guid.NewGuid().ToString("N"));

    public TemporaryWorkspace() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
