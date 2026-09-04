namespace TodoApp.Eval.Tests;

public sealed class CalibrationTests
{
    [Fact]
    public void Agentic_v2_pack_has_three_runs_and_all_required_contrast_categories()
    {
        var profile = SemanticTestData.Profile();
        var pack = CalibrationPack.Load(Path.Combine(AppContext.BaseDirectory, "calibration", "agentic-v2", "cases.json"));

        pack.ValidateAgainst(profile);

        Assert.Equal(3, pack.RunsPerFixture);
        Assert.True(pack.Cases.Count >= 18);
        Assert.Contains(pack.Cases, item => item.Id.Contains("adequate", StringComparison.Ordinal));
        Assert.Contains(pack.Cases, item => item.Id.Contains("exemplary", StringComparison.Ordinal));
        Assert.Contains(pack.Cases, item => item.PerturbationOf is not null);
        Assert.All(pack.Cases, item => Assert.Equal(6, item.DimensionRanges.Count));
    }

    [Fact]
    public void Verifier_accepts_three_stable_in_range_runs_and_expected_high_finding()
    {
        const string criterion = "functional.contract-completeness";
        var expected = new ExpectedSemanticFinding(criterion, SemanticFindingSeverity.High);
        var item = Case("defect", expectedFindings: [expected], range: new(SemanticLevel.Failed, SemanticLevel.Failed));
        var pack = Pack(item);
        var finding = SemanticTestData.Finding("F-1", criterion, SemanticFindingSeverity.High);
        var grades = Enumerable.Range(0, 3).Select(_ => Grade(0, SemanticLevel.Failed, [finding], gatesPass: false)).ToArray();

        var result = CalibrationVerifier.Verify(pack,
            new Dictionary<string, IReadOnlyList<SemanticGrade>> { [item.Id] = grades });

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures));
        Assert.Equal(1, result.InRangeRate);
        Assert.Equal(3, result.RunsChecked);
    }

    [Fact]
    public void Verifier_rejects_missed_and_false_high_findings()
    {
        const string expectedCriterion = "functional.contract-completeness";
        var item = Case("bad", [new(expectedCriterion, SemanticFindingSeverity.High)]);
        var falseHigh = SemanticTestData.Finding("F-false", "operations.security", SemanticFindingSeverity.High);
        var grades = Enumerable.Range(0, 3)
            .Select(_ => Grade(50, SemanticLevel.AdequateWithGaps, [falseHigh], gatesPass: false)).ToArray();

        var result = CalibrationVerifier.Verify(Pack(item),
            new Dictionary<string, IReadOnlyList<SemanticGrade>> { [item.Id] = grades });

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, failure => failure.Contains("was missed", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("false high", StringComparison.Ordinal));
    }

    [Fact]
    public void Verifier_enforces_variance_perturbation_and_absolute_ordering()
    {
        var weak = Case("weak", range: new(SemanticLevel.Failed, SemanticLevel.Exemplary));
        var strong = Case("strong", range: new(SemanticLevel.Failed, SemanticLevel.Exemplary), betterThan: ["weak"]);
        var perturbation = Case("rename", range: new(SemanticLevel.Failed, SemanticLevel.Exemplary), perturbationOf: "strong");
        var pack = Pack(weak, strong, perturbation);
        var grades = new Dictionary<string, IReadOnlyList<SemanticGrade>>
        {
            ["weak"] = [Grade(80), Grade(80), Grade(80)],
            ["strong"] = [Grade(70), Grade(80), Grade(90)],
            ["rename"] = [Grade(60), Grade(60), Grade(60, gatesPass: false)]
        };

        var result = CalibrationVerifier.Verify(pack, grades);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, failure => failure.Contains("standard deviation", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("perturbation score", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("pass/fail flip", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("ordering", StringComparison.Ordinal));
    }

    [Fact]
    public void Agentic_v1_calibration_pack_remains_loadable()
    {
        var profile = ReviewProfile.Load(AppContext.BaseDirectory, "agentic-v1");
        var pack = CalibrationPack.Load(Path.Combine(AppContext.BaseDirectory, "calibration", "agentic-v1", "cases.json"));

        pack.ValidateAgainst(profile);

        Assert.Equal(1, pack.RunsPerFixture);
        Assert.Equal(9, pack.Cases.Count);
    }

    private static CalibrationPack Pack(params CalibrationCase[] cases) => new(
        "pack", "agentic-v2", new("gpt-5.6-terra", "high"), new("gpt-5.6-sol", "high"), 3, cases);

    private static CalibrationCase Case(
        string id,
        IReadOnlyList<ExpectedSemanticFinding>? expectedFindings = null,
        SemanticLevelRange? range = null,
        IReadOnlyList<string>? betterThan = null,
        string? perturbationOf = null) => new(
        id, "fixture", range ?? new(SemanticLevel.AdequateWithGaps, SemanticLevel.AdequateWithGaps),
        new Dictionary<string, SemanticLevelRange>(),
        new Dictionary<string, DecimalRange> { ["functional"] = new(0, 30) },
        expectedFindings ?? [], [], betterThan ?? [], perturbationOf);

    private static SemanticGrade Grade(
        decimal quality,
        SemanticLevel level = SemanticLevel.Strong,
        IReadOnlyList<SemanticFinding>? findings = null,
        bool gatesPass = true)
    {
        var criterion = SemanticTestData.Criterion(
            "functional.contract-completeness", "functional", 20, level,
            findings?.Select(x => x.Id).ToArray());
        return new(
            "agentic-v2", "hash", quality, 100,
            [new("functional", "Functional behavior", 30, quality * 0.3m)], [criterion],
            [new("requiredBehaviorComplete", gatesPass, "reason"), new("noCriticalSemanticFinding", gatesPass, "reason")],
            findings ?? [], new(["source.cs"], []), "summary", [], [], []);
    }
}
