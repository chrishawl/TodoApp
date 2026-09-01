using System.Text.Json;

namespace TodoApp.Eval.Tests;

public sealed class SemanticGradingTests
{
    [Fact]
    public void Agentic_profile_has_fifteen_applicable_criteria_and_thirty_points()
    {
        var profile = SemanticTestData.Profile();

        Assert.Equal("agentic-v1", profile.Rubric.Id);
        Assert.Equal(15, profile.Rubric.Criteria.Count);
        Assert.Equal(15, profile.Rubric.ApplicableCriterionIds.Count);
        Assert.Equal(30, profile.Rubric.SemanticPoints);
        Assert.Equal(64, profile.Hash.Length);
    }

    [Fact]
    public void Missing_criterion_is_rejected()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var candidate = SemanticTestData.Candidate(workspace.Path, profile);
        var input = SemanticTestData.Input(profile);

        var error = Assert.Throws<HarnessException>(() => SemanticGradeValidator.ValidateAndScore(
            profile, candidate, input with { Criteria = input.Criteria.Skip(1).ToArray() }));

        Assert.Contains("omitted criteria", error.Message);
    }

    [Fact]
    public void Duplicate_criterion_is_rejected()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var candidate = SemanticTestData.Candidate(workspace.Path, profile);
        var input = SemanticTestData.Input(profile);

        var error = Assert.Throws<HarnessException>(() => SemanticGradeValidator.ValidateAndScore(
            profile, candidate, input with { Criteria = [.. input.Criteria, input.Criteria[0]] }));

        Assert.Contains("duplicate criterion", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_outside_frozen_worktree_is_rejected()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var candidate = SemanticTestData.Candidate(workspace.Path, profile);
        var input = SemanticTestData.Input(profile);
        var first = input.Criteria[0] with
        {
            Evidence = [new(EvidenceKind.Source, "escaped evidence", "../outside.cs", 1)]
        };

        var error = Assert.Throws<HarnessException>(() => SemanticGradeValidator.ValidateAndScore(
            profile, candidate, input with { Criteria = [first, .. input.Criteria.Skip(1)] }));

        Assert.Contains("escapes", error.Message);
    }

    [Fact]
    public void Source_evidence_through_a_symlink_is_rejected()
    {
        using var workspace = SemanticTestData.Workspace();
        var outside = Path.Combine(Path.GetTempPath(), "todo-eval-outside-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(outside, "// outside\n");
        try
        {
            File.CreateSymbolicLink(Path.Combine(workspace.Path, "link.cs"), outside);
            var profile = SemanticTestData.Profile();
            var candidate = SemanticTestData.Candidate(workspace.Path, profile);
            var input = SemanticTestData.Input(profile);
            var first = input.Criteria[0] with
            {
                Evidence = [new(EvidenceKind.Source, "symlink evidence", "link.cs", 1)]
            };

            var error = Assert.Throws<HarnessException>(() => SemanticGradeValidator.ValidateAndScore(
                profile, candidate, input with { Criteria = [first, .. input.Criteria.Skip(1)] }));

            Assert.Contains("symlink", error.Message);
        }
        finally
        {
            if (File.Exists(outside)) File.Delete(outside);
        }
    }

    [Fact]
    public void Command_evidence_must_match_the_observed_grader_trace()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var candidate = SemanticTestData.Candidate(workspace.Path, profile);
        var input = SemanticTestData.Input(profile);
        const string command = "dotnet test --no-build --filter Search";
        var first = input.Criteria[0] with
        {
            Evidence = [new(EvidenceKind.Command, "Focused verification passed.", Command: command)]
        };
        input = input with { Criteria = [first, .. input.Criteria.Skip(1)] };

        Assert.Throws<HarnessException>(() => SemanticGradeValidator.ValidateAndScore(profile, candidate, input));
        var grade = SemanticGradeValidator.ValidateAndScore(profile, candidate, input, [command]);

        Assert.Equal(30, grade.Score);
    }

    [Fact]
    public void Applicability_is_normalized_to_thirty_points()
    {
        using var workspace = SemanticTestData.Workspace();
        var rubric = new RubricProfile(
            "normalization", "1", "task", 30,
            new Dictionary<string, int> { ["met"] = 2, ["partial"] = 1, ["notMet"] = 0 },
            ["one", "two"],
            [new("one", "a", "one"), new("two", "a", "two"), new("three", "b", "three")],
            []);
        var profile = new ReviewProfile(rubric, "{}", "prompt", "{}", "hash");
        var candidate = SemanticTestData.Candidate(workspace.Path, profile);
        var input = SemanticTestData.Input(profile, id => id switch
        {
            "one" => SemanticVerdict.Met,
            "two" => SemanticVerdict.Partial,
            _ => SemanticVerdict.NotApplicable
        });

        var grade = SemanticGradeValidator.ValidateAndScore(profile, candidate, input);

        Assert.Equal(3, grade.RawPoints);
        Assert.Equal(4, grade.ApplicableRawPoints);
        Assert.Equal(23, grade.Score);
        Assert.Null(grade.Criteria.Single(x => x.CriterionId == "three").Points);
    }

    [Fact]
    public void High_and_medium_findings_apply_deterministic_caps()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var candidate = SemanticTestData.Candidate(workspace.Path, profile);
        var highCriterion = profile.Rubric.Criteria[0].Id;
        var mediumCriterion = profile.Rubric.Criteria[1].Id;
        var findings = new[]
        {
            SemanticTestData.Finding("F-high", highCriterion, SemanticFindingSeverity.High),
            SemanticTestData.Finding("F-medium", mediumCriterion, SemanticFindingSeverity.Medium)
        };
        var input = SemanticTestData.Input(profile, _ => SemanticVerdict.Met, findings);

        var grade = SemanticGradeValidator.ValidateAndScore(profile, candidate, input);

        Assert.Equal(SemanticVerdict.NotMet, grade.Criteria[0].Verdict);
        Assert.Equal(0, grade.Criteria[0].Points);
        Assert.Equal(SemanticVerdict.Partial, grade.Criteria[1].Verdict);
        Assert.Equal(1, grade.Criteria[1].Points);
        Assert.Equal(27, grade.Score);
    }

    [Fact]
    public async Task One_agent_call_returns_scored_grade_and_publishes_telemetry()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var input = SemanticTestData.Input(profile);
        var telemetry = SemanticTestData.Telemetry("gpt-5.6-terra", "high");
        var executor = new StubAgentExecutor(JsonSerializer.Serialize(input, Json.Options), telemetry);
        var artifacts = Path.Combine(workspace.Path, "artifacts");
        var candidate = SemanticTestData.Candidate(workspace.Path, profile, ["source.cs"]);
        AgentTelemetry? observed = null;
        var grader = new SemanticGrading(executor,
            new(CliProvider.Codex, "gpt-5.6-terra", ReasoningEffort: "high"),
            profile, artifacts, GraderBudget.AgenticV1, value => observed = value);

        var grade = await grader.GradeAsync(candidate, CancellationToken.None);

        Assert.Equal(1, executor.Calls);
        Assert.Same(telemetry, observed);
        Assert.Equal(30, grade.Score);
        Assert.True(File.Exists(Path.Combine(artifacts, "semantic-grade.json")));
        Assert.DoesNotContain("privateTests", executor.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("acceptanceGroups", executor.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("privateAcceptanceTests", executor.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("authenticationIsolation", executor.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Path, executor.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_owned_score_field_is_rejected()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var valid = JsonSerializer.Serialize(SemanticTestData.Input(profile), Json.Options);
        var response = valid.Insert(valid.LastIndexOf('}'), ",\"total\":30");
        var executor = new StubAgentExecutor(response, SemanticTestData.Telemetry("gpt-5.6-terra", "high"));
        var grader = new SemanticGrading(executor,
            new(CliProvider.Codex, "gpt-5.6-terra", ReasoningEffort: "high"),
            profile, Path.Combine(workspace.Path, "artifacts"), GraderBudget.AgenticV1);

        var error = await Assert.ThrowsAsync<HarnessException>(() =>
            grader.GradeAsync(SemanticTestData.Candidate(workspace.Path, profile), CancellationToken.None));

        Assert.Contains("fixed contract", error.Message);
    }

    [Fact]
    public async Task Numeric_verdict_is_rejected_by_the_fixed_contract()
    {
        using var workspace = SemanticTestData.Workspace();
        var profile = SemanticTestData.Profile();
        var valid = JsonSerializer.Serialize(SemanticTestData.Input(profile), Json.Options);
        var response = valid.Replace("\"verdict\": \"met\"", "\"verdict\": 0", StringComparison.Ordinal);
        var executor = new StubAgentExecutor(response, SemanticTestData.Telemetry("gpt-5.6-terra", "high"));
        var grader = new SemanticGrading(executor,
            new(CliProvider.Codex, "gpt-5.6-terra", ReasoningEffort: "high"),
            profile, Path.Combine(workspace.Path, "artifacts"), GraderBudget.AgenticV1);

        await Assert.ThrowsAsync<HarnessException>(() =>
            grader.GradeAsync(SemanticTestData.Candidate(workspace.Path, profile), CancellationToken.None));
    }

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
    public static ReviewProfile Profile() => ReviewProfile.Load(AppContext.BaseDirectory, "agentic-v1");

    public static TemporaryWorkspace Workspace()
    {
        var workspace = new TemporaryWorkspace();
        File.WriteAllText(Path.Combine(workspace.Path, "source.cs"), "// source\n");
        return workspace;
    }

    public static GradingCase Candidate(string worktree, ReviewProfile profile, IReadOnlyList<string>? changedFiles = null) => new(
        "task",
        "architecture",
        worktree,
        "base",
        new string('a', 64),
        changedFiles ?? ["source.cs"],
        new(true, true, new(1, 0, 0, 1, true), true, true, true, true, new(80, 70),
            new(changedFiles ?? ["source.cs"], 1, 0, true, true, true, true, true, true, true)),
        profile.Rubric.Id,
        profile.Hash);

    public static SemanticGradeInput Input(
        ReviewProfile profile,
        Func<string, SemanticVerdict>? verdict = null,
        IReadOnlyList<SemanticFinding>? findings = null)
    {
        verdict ??= _ => SemanticVerdict.Met;
        findings ??= [];
        var findingIds = findings.GroupBy(x => x.CriterionId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Select(y => y.Id).ToArray(), StringComparer.Ordinal);
        var criteria = profile.Rubric.Criteria.Select(criterion => new CriterionVerdict(
            criterion.Id,
            verdict(criterion.Id),
            "Traceable rationale.",
            [new(EvidenceKind.Source, "Source establishes the criterion.", "source.cs", 1)],
            findingIds.GetValueOrDefault(criterion.Id) ?? [])).ToArray();
        return new(criteria, findings, new(["source.cs"], []), "Evidence-backed summary.", ["strength"], [], ["recommendation"]);
    }

    public static SemanticFinding Finding(string id, string criterionId, SemanticFindingSeverity severity) => new(
        id,
        criterionId,
        severity,
        "A realistic trigger occurs.",
        "Observable behavior is incorrect.",
        "source.cs",
        1,
        [new(EvidenceKind.Source, "The source shows the behavior.", "source.cs", 1)]);

    public static SemanticGrade Grade(int score = 30) => new(
        "agentic-v1", "hash", score, 30, score, 30, [], [], new(["source.cs"], []),
        "summary", ["strength"], [], ["recommendation"]);

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
