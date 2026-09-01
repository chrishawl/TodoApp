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
    public void Score_arithmetic_is_exact_and_hard_gate_takes_precedence()
    {
        var deterministic = Deterministic(allAcceptance: true, failedGate: "noSecrets");
        var grade = Scoring.Calculate(deterministic, SemanticTestData.Grade(), agentSucceeded: true);
        Assert.Equal(96, grade.Score); Assert.Equal("FAIL", grade.Status);
    }

    [Fact]
    public void Semantic_points_are_owned_by_the_scored_grade()
    {
        var semantic = SemanticTestData.Grade(17);

        var grade = Scoring.Calculate(Deterministic(true), semantic, true);

        Assert.Equal(17, grade.SemanticPoints);
        Assert.Equal(87, grade.Score);
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
                    <UnitTestResult testName="Namespace.Tests.authenticationIsolation_one" outcome="Passed" />
                    <UnitTestResult testName="Namespace.Tests.filtering_one" outcome="Passed" />
                    <UnitTestResult testName="Namespace.Tests.paginationContract_one" outcome="Passed" />
                    <UnitTestResult testName="Namespace.Tests.validationRegression_one(value: &quot;x&quot;)" outcome="Passed" />
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
