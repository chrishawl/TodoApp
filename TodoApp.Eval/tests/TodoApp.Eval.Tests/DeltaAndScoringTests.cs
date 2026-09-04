namespace TodoApp.Eval.Tests;

public sealed class DeltaAndScoringTests
{
    [Fact]
    public void Diagnostic_delta_ignores_line_moves_and_fixed_debt()
    {
        var baseline = new[] { new DiagnosticFinding("warning", "CA1000", "A.cs", "same"), new DiagnosticFinding("warning", "CA2000", "B.cs", "fixed") };
        var candidate = new[] { new DiagnosticFinding("warning", "CA1000", "A.cs", "same"), new DiagnosticFinding("warning", "CA3000", "C.cs", "new") };
        var delta = DeterministicEvaluator.MultisetDelta(candidate, baseline, DeterministicEvaluator.FindingKey).ToArray();
        Assert.Single(delta); Assert.Equal("CA3000", delta[0].Rule);
    }

    [Fact]
    public void Analyzer_build_forces_compilation_after_an_agent_build()
    {
        var command = DeterministicEvaluator.AnalyzerBuildSpec("/workspace", "/workspace/TodoApp.sln");

        Assert.Equal("dotnet", command.FileName);
        Assert.Contains("--no-incremental", command.Arguments);
        Assert.Contains("-p:EnableNETAnalyzers=true", command.Arguments);
        Assert.Contains("-p:AnalysisLevel=latest-recommended", command.Arguments);
        Assert.Contains("-p:AnalysisMode=All", command.Arguments);
    }

    [Fact]
    public void Hard_gate_takes_precedence_over_semantic_quality()
    {
        var deterministic = Deterministic(allAcceptance: true, failedGate: "noSecrets");
        var grade = Scoring.Calculate(deterministic, SemanticTestData.Grade(), agentSucceeded: true);
        Assert.Equal("FAIL", grade.Status);
    }

    [Fact]
    public void Semantic_quality_is_independent_from_deterministic_attainment()
    {
        var semantic = SemanticTestData.Grade(57m);

        var grade = Scoring.Calculate(Deterministic(true), semantic, true);

        Assert.Equal(50, grade.AcceptancePoints);
        Assert.Equal(20, grade.EngineeringPoints);
        Assert.Equal(57m, semantic.QualityScore);
    }

    [Fact]
    public void Semantic_gate_failure_overrides_a_passing_deterministic_result()
    {
        var semantic = SemanticTestData.Grade(gatesPass: false);

        var grade = Scoring.Calculate(Deterministic(true), semantic, true);

        Assert.Equal("FAIL", grade.Status);
        Assert.False(grade.HardGates["requiredBehaviorComplete"]);
        Assert.Contains(grade.Failures, failure => failure.Contains("requiredBehaviorComplete", StringComparison.Ordinal));
    }

    [Fact]
    public void Acceptance_groups_recognize_fully_qualified_trx_test_names()
    {
        var path = Path.Combine(Path.GetTempPath(), "todo-eval-trx-" + Guid.NewGuid().ToString("N") + ".trx");
        try
        {
            File.WriteAllText(path, """
                <TestRun>
                  <Results>
                    <UnitTestResult testName="Namespace.Tests.AuthenticationIsolationRequiresUser" outcome="Passed" />
                    <UnitTestResult testName="Namespace.Tests.FilteringMatchesQuery" outcome="Passed" />
                    <UnitTestResult testName="Namespace.Tests.PaginationContractReturnsPage" outcome="Passed" />
                    <UnitTestResult testName="Namespace.Tests.ValidationRegressionRejectsValue(value: &quot;x&quot;)" outcome="Passed" />
                  </Results>
                </TestRun>
                """);
            var groups = DeterministicEvaluator.ParseAcceptanceGroups(path);
            Assert.All(groups, group => Assert.True(group.Value, group.Key));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static DeterministicResult Deterministic(bool allAcceptance, string? failedGate = null)
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
        if (failedGate is not null) gates[failedGate] = false;
        var acceptance = new Dictionary<string, bool>
        {
            ["authenticationIsolation"] = allAcceptance,
            ["filtering"] = allAcceptance,
            ["paginationContract"] = allAcceptance,
            ["validationRegression"] = allAcceptance
        };
        return new([], [], [], [], new(1, 0, 0, 1, true), new(1, 0, 0, 1, true), new(80, 70),
            new([], 0, 0, 1, 1, 0, [], [], [], [], [], [], true, true, true), acceptance, gates, false);
    }
}
