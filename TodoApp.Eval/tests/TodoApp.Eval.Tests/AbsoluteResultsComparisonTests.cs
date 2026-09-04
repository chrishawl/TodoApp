namespace TodoApp.Eval.Tests;

public sealed class AbsoluteResultsComparisonTests
{
    [Fact]
    public void Comparison_groups_compatible_runs_and_reports_absolute_dimension_statistics()
    {
        using var workspace = SemanticTestData.Workspace();
        WriteRun(workspace.Path, "run-1", "model-a", 80, 24);
        WriteRun(workspace.Path, "run-2", "model-a", 90, 27);
        WriteRun(workspace.Path, "run-3", "model-b", 70, 21);

        var report = AbsoluteResultsComparison.Create(workspace.Path);

        Assert.Contains("No additional judge was invoked", report);
        Assert.Contains("`codex/model-a/high` | 2 | 85.0 | 80.0–90.0 | 25.5", report);
        Assert.Contains("`codex/model-b/high` | 1 | 70.0 | 70.0–70.0 | 21.0", report);
        Assert.Contains("### `functional`", report);
        Assert.Contains("descriptive only, not general evidence", report);
    }

    [Theory]
    [InlineData("different-task", "base", "profile")]
    [InlineData("task", "different-base", "profile")]
    [InlineData("task", "base", "different-profile")]
    public void Comparison_rejects_incompatible_task_base_or_profile_hashes(
        string taskHash,
        string baseHash,
        string profileHash)
    {
        using var workspace = SemanticTestData.Workspace();
        WriteRun(workspace.Path, "run-1", "model-a", 80, 24);
        WriteRun(workspace.Path, "run-2", "model-b", 90, 27, taskHash, baseHash, profileHash);

        var error = Assert.Throws<HarnessException>(() => AbsoluteResultsComparison.Create(workspace.Path));

        Assert.Contains("task, base-commit, or review-profile", error.Message);
    }

    private static void WriteRun(
        string root,
        string runId,
        string model,
        decimal quality,
        int composite,
        string taskHash = "task",
        string baseHash = "base",
        string profileHash = "profile")
    {
        var directory = Path.Combine(root, runId);
        Directory.CreateDirectory(directory);
        var spec = new AgentSpec(CliProvider.Codex, model, ReasoningEffort: "high");
        var now = DateTimeOffset.UnixEpoch;
        Json.Write(Path.Combine(directory, "manifest.json"), new RunManifest(
            runId, "/repo", "base", baseHash, "/worktree", directory, "9.0.305",
            spec, new(CliProvider.Codex, "gpt-5.6-terra", ReasoningEffort: "high"),
            "agentic-v2", profileHash, GraderBudget.AgenticV2, now, now, false, TaskHash: taskHash));
        Json.Write(Path.Combine(directory, "semantic-grade.json"), new SemanticGrade(
            "agentic-v2", profileHash, quality, 100, composite, 30,
            [new("functional", "Functional behavior", 30, quality * 0.3m)], [],
            [new("requiredBehaviorComplete", true, "complete"), new("noCriticalSemanticFinding", true, "clear")],
            [], new(["source.cs"], []), "summary", [], [], []));
    }
}
