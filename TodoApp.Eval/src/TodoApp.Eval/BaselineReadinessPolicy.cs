namespace TodoApp.Eval;

internal static class BaselineReadinessPolicy
{
    public static BaselineReadiness Assess(
        string resolvedCommit,
        string dotnetSdk,
        bool restorePassed,
        DeterministicResult? baseline,
        int expectedTestCount = TaskDefinition.ExpectedBaselineTestCount)
    {
        var failures = new List<string>();
        if (!restorePassed) failures.Add("Dependency restore failed.");

        var buildPassed = baseline is not null && Gate(baseline, "build");
        if (!buildPassed) failures.Add("Analyzer-enabled build did not pass.");

        var tests = baseline?.ExistingTests ?? new TestSummary(0, 0, 0, 0, false);
        var total = tests.Passed + tests.Failed + tests.Skipped;
        var existingTestsPassed = baseline is not null && Gate(baseline, "existingTests")
            && tests.Discovered && tests.Failed == 0 && tests.Skipped == 0 && total == expectedTestCount;
        if (!tests.Discovered) failures.Add("No existing tests were discovered.");
        else
        {
            if (tests.Failed != 0) failures.Add($"{tests.Failed} existing test(s) failed.");
            if (tests.Skipped != 0) failures.Add($"{tests.Skipped} existing test(s) were skipped.");
            if (total != expectedTestCount) failures.Add($"Expected {expectedTestCount} existing tests but discovered {total}.");
        }

        var formatPassed = baseline is not null && Gate(baseline, "format") && baseline.FormatFindings.Count == 0;
        if (!formatPassed) failures.Add("dotnet format --verify-no-changes did not pass cleanly.");

        var worktreeChanges = baseline?.Diff.ChangedFiles ?? [];
        var worktreeClean = baseline is not null && worktreeChanges.Count == 0 && baseline.Diff.DiffCheckPassed;
        if (!worktreeClean) failures.Add("Baseline checks left tracked or untracked changes in the worktree.");

        return new(failures.Count == 0, resolvedCommit, dotnetSdk, restorePassed, buildPassed,
            existingTestsPassed, formatPassed, worktreeClean, expectedTestCount, tests, worktreeChanges, failures);
    }

    public static void RequireReady(BaselineReadiness readiness)
    {
        if (!readiness.Ready)
            throw new HarnessException("Baseline is not evaluation-ready: " + string.Join(" ", readiness.Failures));
    }

    private static bool Gate(DeterministicResult result, string name) => result.HardGates.GetValueOrDefault(name);
}
