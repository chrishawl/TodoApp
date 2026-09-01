using System.Text.Json;

namespace TodoApp.Eval;

internal sealed record CalibrationConfiguration(string Model, string ReasoningEffort);

internal sealed record CalibrationCase(
    string Id,
    string Fixture,
    IReadOnlyList<string> MustFind,
    IReadOnlyList<string> MustNotFind,
    IReadOnlyDictionary<string, SemanticVerdict> CriterionCeilings,
    IReadOnlyDictionary<string, SemanticVerdict> CriterionFloors);

internal sealed record CalibrationPack(
    string Id,
    string RubricProfileId,
    CalibrationConfiguration SelectedConfiguration,
    CalibrationConfiguration ComparisonConfiguration,
    IReadOnlyList<CalibrationCase> Cases)
{
    public static CalibrationPack Load(string path)
    {
        try
        {
            var pack = JsonSerializer.Deserialize<CalibrationPack>(File.ReadAllText(path), ReviewProfile.StrictJsonOptions())
                ?? throw new JsonException("null pack");
            if (string.IsNullOrWhiteSpace(pack.Id) || string.IsNullOrWhiteSpace(pack.RubricProfileId) || pack.Cases is null || pack.Cases.Count == 0)
                throw new HarnessException("Calibration pack identity and cases must be populated.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in pack.Cases)
            {
                if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Fixture))
                    throw new HarnessException("Every calibration case needs an id and frozen fixture identity.");
                if (!ids.Add(item.Id)) throw new HarnessException($"Duplicate calibration case '{item.Id}'.");
                if (item.MustFind is null || item.MustNotFind is null || item.CriterionCeilings is null || item.CriterionFloors is null)
                    throw new HarnessException($"Calibration case '{item.Id}' is missing assertions.");
            }
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
        foreach (var item in Cases)
        {
            foreach (var criterionId in item.MustFind.Concat(item.MustNotFind)
                         .Concat(item.CriterionCeilings.Keys).Concat(item.CriterionFloors.Keys))
                if (!criterionIds.Contains(criterionId))
                    throw new HarnessException($"Calibration case '{item.Id}' references unknown criterion '{criterionId}'.");
            foreach (var conflict in item.MustFind.Intersect(item.MustNotFind, StringComparer.Ordinal))
                throw new HarnessException($"Calibration case '{item.Id}' both requires and forbids finding '{conflict}'.");
            if (item.CriterionCeilings.Values.Concat(item.CriterionFloors.Values).Contains(SemanticVerdict.NotApplicable))
                throw new HarnessException($"Calibration case '{item.Id}' cannot use notApplicable as a criterion bound.");
        }
    }
}

internal sealed record CalibrationResult(int CasesChecked, bool Passed, IReadOnlyList<string> Failures);

internal static class CalibrationVerifier
{
    public static CalibrationResult VerifyRun(
        CalibrationPack pack,
        string resultRoot,
        CalibrationConfiguration configuration)
    {
        var assertionResult = Verify(pack, LoadGrades(pack, resultRoot));
        var failures = assertionResult.Failures.ToList();
        foreach (var item in pack.Cases)
        {
            var path = Path.Combine(resultRoot, item.Id, "manifest.json");
            if (!File.Exists(path))
            {
                failures.Add($"{item.Id}: manifest.json is missing.");
                continue;
            }
            var manifest = Json.Read<RunManifest>(path);
            if (!string.Equals(manifest.SemanticGrader.Model, configuration.Model, StringComparison.Ordinal) ||
                !string.Equals(manifest.SemanticGrader.ReasoningEffort, configuration.ReasoningEffort, StringComparison.Ordinal))
                failures.Add($"{item.Id}: manifest grader {manifest.SemanticGrader.Model}/{manifest.SemanticGrader.ReasoningEffort} does not match {configuration.Model}/{configuration.ReasoningEffort}.");
            if (manifest.SemanticGraderTelemetry is null)
                failures.Add($"{item.Id}: semantic grader telemetry is missing from the manifest.");
        }
        return new(pack.Cases.Count, failures.Count == 0, failures);
    }

    public static CalibrationResult Verify(CalibrationPack pack, IReadOnlyDictionary<string, SemanticGrade> grades)
    {
        var failures = new List<string>();
        foreach (var item in pack.Cases)
        {
            if (!grades.TryGetValue(item.Id, out var grade))
            {
                failures.Add($"{item.Id}: semantic-grade.json is missing.");
                continue;
            }
            if (!string.Equals(grade.RubricProfileId, pack.RubricProfileId, StringComparison.Ordinal))
            {
                failures.Add($"{item.Id}: grade uses rubric '{grade.RubricProfileId}'.");
                continue;
            }

            var findings = grade.Findings.Select(x => x.CriterionId).ToHashSet(StringComparer.Ordinal);
            foreach (var criterionId in item.MustFind)
                if (!findings.Contains(criterionId)) failures.Add($"{item.Id}: mustFind '{criterionId}' was missed.");
            foreach (var criterionId in item.MustNotFind)
                if (findings.Contains(criterionId)) failures.Add($"{item.Id}: mustNotFind '{criterionId}' produced a false positive.");

            var criteria = grade.Criteria.ToDictionary(x => x.CriterionId, StringComparer.Ordinal);
            foreach (var (criterionId, ceiling) in item.CriterionCeilings)
                CheckBound(item.Id, criterionId, ceiling, isCeiling: true, criteria, failures);
            foreach (var (criterionId, floor) in item.CriterionFloors)
                CheckBound(item.Id, criterionId, floor, isCeiling: false, criteria, failures);
        }
        return new(pack.Cases.Count, failures.Count == 0, failures);
    }

    public static IReadOnlyDictionary<string, SemanticGrade> LoadGrades(CalibrationPack pack, string resultRoot)
    {
        var grades = new Dictionary<string, SemanticGrade>(StringComparer.Ordinal);
        foreach (var item in pack.Cases)
        {
            var path = Path.Combine(resultRoot, item.Id, "semantic-grade.json");
            if (File.Exists(path)) grades[item.Id] = Json.Read<SemanticGrade>(path);
        }
        return grades;
    }

    private static void CheckBound(
        string caseId,
        string criterionId,
        SemanticVerdict bound,
        bool isCeiling,
        Dictionary<string, SemanticCriterionGrade> criteria,
        List<string> failures)
    {
        if (!criteria.TryGetValue(criterionId, out var grade))
        {
            failures.Add($"{caseId}: criterion '{criterionId}' is missing from the grade.");
            return;
        }
        var actualLevel = Level(grade.Verdict);
        var boundLevel = Level(bound);
        if ((isCeiling && actualLevel > boundLevel) || (!isCeiling && actualLevel < boundLevel))
            failures.Add($"{caseId}: criterion '{criterionId}' verdict {grade.Verdict} violates {(isCeiling ? "ceiling" : "floor")} {bound}.");
    }

    private static int Level(SemanticVerdict verdict) => verdict switch
    {
        SemanticVerdict.NotMet => 0,
        SemanticVerdict.Partial => 1,
        SemanticVerdict.Met => 2,
        _ => throw new HarnessException("Calibration bounds cannot use notApplicable.")
    };
}
