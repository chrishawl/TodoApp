using System.Security.Cryptography;
using System.Text.Json;

namespace TodoApp.Eval;

internal sealed record CalibrationConfiguration(string Model, string ReasoningEffort);
internal sealed record SemanticLevelRange(SemanticLevel Minimum, SemanticLevel Maximum);
internal sealed record DecimalRange(decimal Minimum, decimal Maximum);
internal sealed record ExpectedSemanticFinding(string CriterionId, SemanticFindingSeverity? Severity);

internal sealed record CalibrationCase(
    string Id,
    string Fixture,
    SemanticLevelRange DefaultCriterionRange,
    IReadOnlyDictionary<string, SemanticLevelRange> CriterionRanges,
    IReadOnlyDictionary<string, DecimalRange> DimensionRanges,
    IReadOnlyList<ExpectedSemanticFinding> ExpectedFindings,
    IReadOnlyList<string> ForbiddenFindingCriteria,
    IReadOnlyList<string> ExpectedBetterThan,
    string? PerturbationOf = null)
{
    public SemanticLevelRange RangeFor(string criterionId) =>
        CriterionRanges.GetValueOrDefault(criterionId) ?? DefaultCriterionRange;
}

internal sealed record CalibrationPack(
    string Id,
    string RubricProfileId,
    CalibrationConfiguration SelectedConfiguration,
    CalibrationConfiguration ComparisonConfiguration,
    int RunsPerFixture,
    IReadOnlyList<CalibrationCase> Cases)
{
    public static CalibrationPack Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var pack = document.RootElement.TryGetProperty("runsPerFixture", out _)
                ? JsonSerializer.Deserialize<CalibrationPack>(json, ReviewProfile.StrictJsonOptions())
                : ConvertLegacy(JsonSerializer.Deserialize<LegacyCalibrationPack>(json, ReviewProfile.StrictJsonOptions()));
            if (pack is null) throw new JsonException("null pack");
            ValidateShape(pack);
            return pack;
        }
        catch (JsonException ex)
        {
            throw new HarnessException($"Could not parse calibration pack '{path}'.", ex);
        }
    }

    public void ValidateAgainst(ReviewProfile profile)
    {
        if (!string.Equals(RubricProfileId, profile.Rubric.Id, StringComparison.Ordinal))
            throw new HarnessException($"Calibration pack '{Id}' targets '{RubricProfileId}', not '{profile.Rubric.Id}'.");
        var criterionIds = profile.Rubric.Criteria.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var dimensionIds = profile.Rubric.Dimensions.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var caseIds = Cases.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in Cases)
        {
            ValidateRange(item.DefaultCriterionRange, $"case '{item.Id}' default criterion range");
            foreach (var (criterionId, range) in item.CriterionRanges)
            {
                if (!criterionIds.Contains(criterionId))
                    throw new HarnessException($"Calibration case '{item.Id}' references unknown criterion '{criterionId}'.");
                ValidateRange(range, $"case '{item.Id}' criterion '{criterionId}'");
            }
            foreach (var criterionId in criterionIds)
                _ = item.RangeFor(criterionId);
            foreach (var (dimensionId, range) in item.DimensionRanges)
            {
                if (!dimensionIds.Contains(dimensionId))
                    throw new HarnessException($"Calibration case '{item.Id}' references unknown dimension '{dimensionId}'.");
                if (range.Minimum < 0 || range.Maximum < range.Minimum ||
                    range.Maximum > profile.Rubric.Dimensions.Single(x => x.Id == dimensionId).Weight)
                    throw new HarnessException($"Calibration case '{item.Id}' has an invalid range for dimension '{dimensionId}'.");
            }
            if (item.DimensionRanges.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(dimensionIds) is false)
                throw new HarnessException($"Calibration case '{item.Id}' must define a range for every dimension.");
            foreach (var expected in item.ExpectedFindings)
                if (!criterionIds.Contains(expected.CriterionId))
                    throw new HarnessException($"Calibration case '{item.Id}' expects a finding for unknown criterion '{expected.CriterionId}'.");
            foreach (var criterionId in item.ForbiddenFindingCriteria)
                if (!criterionIds.Contains(criterionId))
                    throw new HarnessException($"Calibration case '{item.Id}' forbids a finding for unknown criterion '{criterionId}'.");
            foreach (var other in item.ExpectedBetterThan)
                if (!caseIds.Contains(other) || string.Equals(other, item.Id, StringComparison.Ordinal))
                    throw new HarnessException($"Calibration case '{item.Id}' has invalid ordering contrast '{other}'.");
            if (item.PerturbationOf is not null && (!caseIds.Contains(item.PerturbationOf) || item.PerturbationOf == item.Id))
                throw new HarnessException($"Calibration case '{item.Id}' has invalid perturbation baseline '{item.PerturbationOf}'.");
        }
    }

    private static void ValidateShape(CalibrationPack pack)
    {
        if (string.IsNullOrWhiteSpace(pack.Id) || string.IsNullOrWhiteSpace(pack.RubricProfileId) ||
            pack.RunsPerFixture < 1 || pack.Cases is null || pack.Cases.Count == 0)
            throw new HarnessException("Calibration pack identity, run count, and cases must be populated.");
        if (string.Equals(pack.RubricProfileId, "agentic-v2", StringComparison.Ordinal) && pack.RunsPerFixture != 3)
            throw new HarnessException("agentic-v2 calibration requires exactly three runs per fixture.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in pack.Cases)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Fixture))
                throw new HarnessException("Every calibration case needs an id and frozen fixture identity.");
            if (!ids.Add(item.Id)) throw new HarnessException($"Duplicate calibration case '{item.Id}'.");
            if (item.DefaultCriterionRange is null || item.CriterionRanges is null || item.DimensionRanges is null ||
                item.ExpectedFindings is null || item.ForbiddenFindingCriteria is null || item.ExpectedBetterThan is null)
                throw new HarnessException($"Calibration case '{item.Id}' is missing human-agreed assertions.");
        }
    }

    private static void ValidateRange(SemanticLevelRange range, string owner)
    {
        if (range.Minimum > range.Maximum) throw new HarnessException($"Calibration {owner} has an inverted level range.");
    }

    private static CalibrationPack? ConvertLegacy(LegacyCalibrationPack? legacy)
    {
        if (legacy is null) return null;
        return new(
            legacy.Id,
            legacy.RubricProfileId,
            legacy.SelectedConfiguration,
            legacy.ComparisonConfiguration,
            1,
            legacy.Cases.Select(item => new CalibrationCase(
                item.Id,
                item.Fixture,
                new(SemanticLevel.Failed, SemanticLevel.Exemplary),
                item.CriterionCeilings.Keys.Concat(item.CriterionFloors.Keys).Distinct(StringComparer.Ordinal)
                    .ToDictionary(
                        id => id,
                        id => new SemanticLevelRange(
                            item.CriterionFloors.TryGetValue(id, out var floor) ? Convert(floor) : SemanticLevel.Failed,
                            item.CriterionCeilings.TryGetValue(id, out var ceiling) ? Convert(ceiling) : SemanticLevel.Exemplary),
                        StringComparer.Ordinal),
                new Dictionary<string, DecimalRange> { ["legacy"] = new(0, 30) },
                item.MustFind.Select(id => new ExpectedSemanticFinding(id, null)).ToArray(),
                item.MustNotFind,
                [])).ToArray());
    }

    private static SemanticLevel Convert(LegacyVerdict verdict) => verdict switch
    {
        LegacyVerdict.NotMet => SemanticLevel.Failed,
        LegacyVerdict.Partial => SemanticLevel.AdequateWithGaps,
        LegacyVerdict.Met => SemanticLevel.Strong,
        _ => throw new HarnessException("Legacy calibration bounds cannot use notApplicable.")
    };

    private enum LegacyVerdict { Met, Partial, NotMet, NotApplicable }
    private sealed record LegacyCalibrationCase(
        string Id,
        string Fixture,
        IReadOnlyList<string> MustFind,
        IReadOnlyList<string> MustNotFind,
        IReadOnlyDictionary<string, LegacyVerdict> CriterionCeilings,
        IReadOnlyDictionary<string, LegacyVerdict> CriterionFloors);
    private sealed record LegacyCalibrationPack(
        string Id,
        string RubricProfileId,
        CalibrationConfiguration SelectedConfiguration,
        CalibrationConfiguration ComparisonConfiguration,
        IReadOnlyList<LegacyCalibrationCase> Cases);
}

internal sealed record CalibrationResult(
    int CasesChecked,
    int RunsChecked,
    bool Passed,
    double InRangeRate,
    double AdjacentRangeRate,
    double OrderingPreservationRate,
    IReadOnlyList<string> Failures);

internal static class CalibrationVerifier
{
    public static CalibrationResult VerifyRun(
        CalibrationPack pack,
        string resultRoot,
        CalibrationConfiguration configuration)
    {
        var grades = LoadGrades(pack, resultRoot);
        var assertionResult = Verify(pack, grades);
        var failures = assertionResult.Failures.ToList();
        foreach (var item in pack.Cases)
        {
            var runDirectories = RunDirectories(resultRoot, item.Id).ToArray();
            var fixtureIdentities = new HashSet<string>(StringComparer.Ordinal);
            if (runDirectories.Length != pack.RunsPerFixture)
            {
                failures.Add($"{item.Id}: expected {pack.RunsPerFixture} retained run directories, found {runDirectories.Length}.");
                continue;
            }
            foreach (var directory in runDirectories)
            {
                var path = Path.Combine(directory, "manifest.json");
                if (!File.Exists(path))
                {
                    failures.Add($"{item.Id}/{Path.GetFileName(directory)}: manifest.json is missing.");
                    continue;
                }
                var manifest = Json.Read<RunManifest>(path);
                if (!string.Equals(manifest.RubricProfileId, pack.RubricProfileId, StringComparison.Ordinal))
                    failures.Add($"{item.Id}/{Path.GetFileName(directory)}: manifest uses profile '{manifest.RubricProfileId}'.");
                if (!string.Equals(manifest.SemanticGrader.Model, configuration.Model, StringComparison.Ordinal) ||
                    !string.Equals(manifest.SemanticGrader.ReasoningEffort, configuration.ReasoningEffort, StringComparison.Ordinal))
                    failures.Add($"{item.Id}/{Path.GetFileName(directory)}: manifest grader does not match {configuration.Model}/{configuration.ReasoningEffort}.");
                if (manifest.SemanticGraderTelemetry is null)
                    failures.Add($"{item.Id}/{Path.GetFileName(directory)}: semantic grader telemetry is missing.");

                var gradePath = Path.Combine(directory, "semantic-grade.json");
                if (File.Exists(gradePath))
                {
                    var grade = SemanticGradePersistence.Read(gradePath);
                    if (!string.Equals(grade.RubricProfileId, manifest.RubricProfileId, StringComparison.Ordinal) ||
                        !string.Equals(grade.RubricProfileHash, manifest.RubricProfileHash, StringComparison.Ordinal))
                        failures.Add($"{item.Id}/{Path.GetFileName(directory)}: semantic grade identity does not match its manifest.");
                }

                if (string.Equals(pack.RubricProfileId, "agentic-v2", StringComparison.Ordinal))
                {
                    var taskHash = manifest.TaskHash;
                    var taskPath = Path.Combine(directory, "task.md");
                    if (string.IsNullOrWhiteSpace(taskHash) && File.Exists(taskPath)) taskHash = HashFile(taskPath);
                    var patchPath = Path.Combine(directory, "patch.diff");
                    if (string.IsNullOrWhiteSpace(taskHash) || !File.Exists(patchPath))
                        failures.Add($"{item.Id}/{Path.GetFileName(directory)}: task or patch identity is missing.");
                    else
                        fixtureIdentities.Add($"{taskHash}|{manifest.ResolvedCommit}|{manifest.RubricProfileHash}|{HashFile(patchPath)}");
                }
            }
            if (string.Equals(pack.RubricProfileId, "agentic-v2", StringComparison.Ordinal) && fixtureIdentities.Count > 1)
                failures.Add($"{item.Id}: retained repetitions do not use the same task, base commit, profile, and patch.");
        }
        return assertionResult with { Passed = failures.Count == 0, Failures = failures };
    }

    public static CalibrationResult Verify(
        CalibrationPack pack,
        IReadOnlyDictionary<string, IReadOnlyList<SemanticGrade>> grades)
    {
        var failures = new List<string>();
        var ratings = 0;
        var inside = 0;
        var adjacent = 0;
        var runsChecked = 0;
        foreach (var item in pack.Cases)
        {
            if (!grades.TryGetValue(item.Id, out var caseGrades))
            {
                failures.Add($"{item.Id}: semantic grades are missing.");
                continue;
            }
            if (caseGrades.Count != pack.RunsPerFixture)
                failures.Add($"{item.Id}: expected {pack.RunsPerFixture} semantic grades, found {caseGrades.Count}.");
            runsChecked += caseGrades.Count;
            foreach (var grade in caseGrades)
            {
                if (!string.Equals(grade.RubricProfileId, pack.RubricProfileId, StringComparison.Ordinal))
                {
                    failures.Add($"{item.Id}: grade uses rubric '{grade.RubricProfileId}'.");
                    continue;
                }
                CheckFindings(item, grade, failures);
                var criteria = grade.Criteria.ToDictionary(x => x.CriterionId, StringComparer.Ordinal);
                foreach (var criterion in grade.Criteria)
                {
                    var range = item.RangeFor(criterion.CriterionId);
                    ratings++;
                    if (criterion.Level >= range.Minimum && criterion.Level <= range.Maximum) inside++;
                    if ((int)criterion.Level >= (int)range.Minimum - 1 && (int)criterion.Level <= (int)range.Maximum + 1) adjacent++;
                }
                foreach (var (dimensionId, range) in item.DimensionRanges)
                {
                    if (!grade.Dimensions.Any(x => string.Equals(x.DimensionId, dimensionId, StringComparison.Ordinal)))
                    {
                        failures.Add($"{item.Id}: dimension '{dimensionId}' is missing.");
                        continue;
                    }
                    var actual = grade.Dimensions.Single(x => string.Equals(x.DimensionId, dimensionId, StringComparison.Ordinal)).EarnedPoints;
                    if (actual < range.Minimum || actual > range.Maximum)
                        failures.Add($"{item.Id}: dimension '{dimensionId}' score {actual} is outside {range.Minimum}-{range.Maximum}.");
                }
                foreach (var criterionId in item.CriterionRanges.Keys)
                    if (!criteria.ContainsKey(criterionId)) failures.Add($"{item.Id}: criterion '{criterionId}' is missing.");
            }
            if (caseGrades.Count > 1 && StandardDeviation(caseGrades.Select(x => (double)x.QualityScore)) > 3)
                failures.Add($"{item.Id}: same-patch semantic-score standard deviation exceeds 3 points.");
        }

        var inRangeRate = ratings == 0 ? 0 : (double)inside / ratings;
        var adjacentRate = ratings == 0 ? 0 : (double)adjacent / ratings;
        if (inRangeRate < 0.90) failures.Add($"Only {inRangeRate:P1} of criterion ratings are inside their expected ranges; required 90%.");
        if (adjacentRate < 0.95) failures.Add($"Only {adjacentRate:P1} of criterion ratings are within one adjacent level; required 95%.");

        CheckPerturbations(pack, grades, failures);
        var (preserved, comparisons) = CheckOrdering(pack, grades, failures);
        var orderingRate = comparisons == 0 ? 1 : (double)preserved / comparisons;
        if (orderingRate < 0.90)
            failures.Add($"Only {orderingRate:P1} of expected absolute orderings were preserved; required 90%.");
        return new(pack.Cases.Count, runsChecked, failures.Count == 0, inRangeRate, adjacentRate, orderingRate, failures);
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<SemanticGrade>> LoadGrades(CalibrationPack pack, string resultRoot)
    {
        var grades = new Dictionary<string, IReadOnlyList<SemanticGrade>>(StringComparer.Ordinal);
        foreach (var item in pack.Cases)
        {
            var values = RunDirectories(resultRoot, item.Id)
                .Select(directory => Path.Combine(directory, "semantic-grade.json"))
                .Where(File.Exists)
                .Select(SemanticGradePersistence.Read)
                .ToArray();
            if (values.Length > 0) grades[item.Id] = values;
        }
        return grades;
    }

    private static IEnumerable<string> RunDirectories(string resultRoot, string caseId)
    {
        var caseRoot = Path.Combine(resultRoot, caseId);
        if (!Directory.Exists(caseRoot)) yield break;
        if (File.Exists(Path.Combine(caseRoot, "semantic-grade.json")))
        {
            yield return caseRoot;
            yield break;
        }
        foreach (var directory in Directory.GetDirectories(caseRoot).Order(StringComparer.Ordinal))
            if (File.Exists(Path.Combine(directory, "semantic-grade.json")) || File.Exists(Path.Combine(directory, "manifest.json")))
                yield return directory;
    }

    private static void CheckFindings(CalibrationCase item, SemanticGrade grade, List<string> failures)
    {
        foreach (var expected in item.ExpectedFindings)
        {
            if (!grade.Findings.Any(x => string.Equals(x.CriterionId, expected.CriterionId, StringComparison.Ordinal) &&
                    (expected.Severity is null || x.Severity == expected.Severity)))
            {
                var severity = expected.Severity is { } value ? value.ToString().ToLowerInvariant() + " " : "";
                failures.Add($"{item.Id}: expected {severity}finding for '{expected.CriterionId}' was missed.");
            }
        }
        foreach (var criterionId in item.ForbiddenFindingCriteria)
            if (grade.Findings.Any(x => string.Equals(x.CriterionId, criterionId, StringComparison.Ordinal)))
                failures.Add($"{item.Id}: forbidden finding for '{criterionId}' was reported.");
        var expectedHigh = item.ExpectedFindings.Where(x => x.Severity == SemanticFindingSeverity.High)
            .Select(x => x.CriterionId).ToHashSet(StringComparer.Ordinal);
        foreach (var finding in grade.Findings.Where(x => x.Severity == SemanticFindingSeverity.High))
            if (!expectedHigh.Contains(finding.CriterionId))
                failures.Add($"{item.Id}: false high-severity finding '{finding.Id}' was reported for '{finding.CriterionId}'.");
    }

    private static void CheckPerturbations(
        CalibrationPack pack,
        IReadOnlyDictionary<string, IReadOnlyList<SemanticGrade>> grades,
        List<string> failures)
    {
        foreach (var item in pack.Cases.Where(x => x.PerturbationOf is not null))
        {
            if (!grades.TryGetValue(item.Id, out var changed) || !grades.TryGetValue(item.PerturbationOf!, out var baseline) ||
                changed.Count == 0 || baseline.Count == 0) continue;
            if (changed.Count != baseline.Count || changed.Zip(baseline)
                    .Any(pair => Math.Abs(pair.First.QualityScore - pair.Second.QualityScore) > 3))
                failures.Add($"{item.Id}: perturbation score moved more than 3 points from '{item.PerturbationOf}'.");
            if (changed.Count != baseline.Count || changed.Zip(baseline).Any(pair => Passed(pair.First) != Passed(pair.Second)))
                failures.Add($"{item.Id}: perturbation caused a pass/fail flip relative to '{item.PerturbationOf}'.");
        }
    }

    private static (int Preserved, int Comparisons) CheckOrdering(
        CalibrationPack pack,
        IReadOnlyDictionary<string, IReadOnlyList<SemanticGrade>> grades,
        List<string> failures)
    {
        var preserved = 0;
        var comparisons = 0;
        foreach (var item in pack.Cases)
            foreach (var worseId in item.ExpectedBetterThan)
            {
                if (!grades.TryGetValue(item.Id, out var better) || !grades.TryGetValue(worseId, out var worse) ||
                    better.Count == 0 || worse.Count == 0) continue;
                comparisons++;
                if (Mean(better) > Mean(worse)) preserved++;
                else failures.Add($"{item.Id}: expected absolute ordering over '{worseId}' was not preserved.");
            }
        return (preserved, comparisons);
    }

    private static bool Passed(SemanticGrade grade) => grade.Gates.All(x => x.Passed);
    private static double Mean(IEnumerable<SemanticGrade> grades) => grades.Average(x => (double)x.QualityScore);

    private static double StandardDeviation(IEnumerable<double> values)
    {
        var samples = values.ToArray();
        if (samples.Length == 0) return 0;
        var mean = samples.Average();
        return Math.Sqrt(samples.Average(value => Math.Pow(value - mean, 2)));
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
