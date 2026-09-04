namespace TodoApp.Eval.Tests;

public sealed class BaselineReadinessTests
{
    [Fact]
    public void Clean_baseline_is_ready()
    {
        var readiness = BaselineReadinessPolicy.Assess("commit", "9.0.305", true, CleanBaseline());

        Assert.True(readiness.Ready);
        Assert.Empty(readiness.Failures);
        Assert.Equal(16, readiness.ExistingTests.Passed);
    }

    [Fact]
    public void Restore_failure_stops_pipeline()
    {
        var readiness = BaselineReadinessPolicy.Assess("commit", "9.0.305", false, null);

        Assert.False(readiness.Ready);
        Assert.Contains(readiness.Failures, failure => failure.Contains("restore", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<HarnessException>(() => BaselineReadinessPolicy.RequireReady(readiness));
    }

    [Fact]
    public void Build_failure_is_not_ready()
    {
        var baseline = WithGate(CleanBaseline(), "build", false);

        AssertFailure(baseline, "build");
    }

    [Fact]
    public void Analyzer_diagnostic_is_not_ready()
    {
        var baseline = CleanBaseline() with
        {
            AnalyzerFindings = [new("warning", "CA1000", "File.cs", "Fix the code.")]
        };

        AssertFailure(baseline, "diagnostic");
    }

    [Fact]
    public void Vulnerable_package_is_not_ready()
    {
        var baseline = CleanBaseline() with
        {
            Vulnerabilities = [new("Package", "1.0.0", "https://example.invalid/advisory", "high")]
        };

        AssertFailure(baseline, "vulnerable package");
    }

    [Fact]
    public void Failed_vulnerability_audit_is_not_ready()
    {
        var baseline = WithGate(CleanBaseline(), "noNewVulnerabilities", false);

        AssertFailure(baseline, "Dependency audit");
        Assert.False(DeterministicEvaluator.IsVulnerabilityAuditClean(
            new("vulnerabilities", false, 1, false, 1, "vulnerabilities.json"), []));
    }

    [Fact]
    public void Failed_existing_test_is_not_ready()
    {
        var baseline = CleanBaseline() with { ExistingTests = new(15, 1, 0, 1, true) };

        AssertFailure(baseline, "failed");
    }

    [Fact]
    public void Skipped_existing_test_is_not_ready()
    {
        var baseline = CleanBaseline() with { ExistingTests = new(15, 0, 1, 1, true) };

        AssertFailure(baseline, "skipped");
    }

    [Fact]
    public void Missing_test_discovery_is_not_ready()
    {
        var baseline = CleanBaseline() with { ExistingTests = new(0, 0, 0, 0, false) };

        AssertFailure(baseline, "discovered");
    }

    [Fact]
    public void Unexpected_test_count_is_not_ready()
    {
        var baseline = CleanBaseline() with { ExistingTests = new(15, 0, 0, 1, true) };

        AssertFailure(baseline, "Expected 16");
    }

    [Fact]
    public void Format_failure_is_not_ready_even_with_no_parsed_delta()
    {
        var baseline = WithGate(CleanBaseline(), "format", false);

        AssertFailure(baseline, "format");
        Assert.False(DeterministicEvaluator.IsFormatClean(new("format", false, 2, false, 1, "format.log"), []));
    }

    [Fact]
    public void Parsed_format_finding_is_not_ready()
    {
        var baseline = CleanBaseline() with { FormatFindings = [new("File.cs", "WHITESPACE", "Fix whitespace.")] };

        AssertFailure(baseline, "format");
    }

    [Fact]
    public void Dirty_worktree_is_not_ready()
    {
        var baseline = CleanBaseline() with
        {
            Diff = CleanBaseline().Diff with { ChangedFiles = ["changed.cs"] }
        };

        AssertFailure(baseline, "worktree");
    }

    [Fact]
    public void Candidate_test_gate_rejects_skips()
    {
        var check = new CheckResult("existing-tests", true, 0, false, 1, "tests.trx");

        Assert.False(DeterministicEvaluator.IsExistingTestRunClean(check, new(15, 0, 1, 1, true)));
    }

    private static void AssertFailure(DeterministicResult baseline, string expected)
    {
        var readiness = BaselineReadinessPolicy.Assess("commit", "9.0.305", true, baseline);
        Assert.False(readiness.Ready);
        Assert.Contains(readiness.Failures, failure => failure.Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.Throws<HarnessException>(() => BaselineReadinessPolicy.RequireReady(readiness));
    }

    private static DeterministicResult WithGate(DeterministicResult baseline, string name, bool value)
    {
        var gates = new Dictionary<string, bool>(baseline.HardGates) { [name] = value };
        return baseline with { HardGates = gates };
    }

    private static DeterministicResult CleanBaseline()
    {
        var gates = new Dictionary<string, bool>
        {
            ["build"] = true,
            ["existingTests"] = true,
            ["format"] = true,
            ["noNewVulnerabilities"] = true
        };
        return new([], [], [], [], new(16, 0, 0, 1, true), new(0, 0, 0, 0, false), new(null, null),
            new([], 0, 0, 0, 0, 0, [], [], [], [], [], [], true, false, false),
            new Dictionary<string, bool>(), gates, true);
    }
}
