using System.Globalization;

namespace TodoApp.Eval;

internal static class Scoring
{
    public static GradeResult Calculate(DeterministicResult deterministic, SemanticGrade semantic, bool agentSucceeded)
    {
        var gates = new Dictionary<string, bool>(deterministic.HardGates, StringComparer.Ordinal)
        {
            ["agentExit"] = agentSucceeded
        };
        foreach (var semanticGate in semantic.Gates) gates[semanticGate.Id] = semanticGate.Passed;
        var acceptance =
            Points(deterministic, "authenticationIsolation", 15) +
            Points(deterministic, "filtering", 12) +
            Points(deterministic, "paginationContract", 13) +
            Points(deterministic, "validationRegression", 10);
        var engineering = 0;
        if (Gate(deterministic, "build")) engineering += 4;
        if (Gate(deterministic, "existingTests")) engineering += 4;
        if (Gate(deterministic, "noNewAnalyzerDiagnostics")) engineering += 2;
        if (Gate(deterministic, "format") && Gate(deterministic, "noNewFormatViolations")) engineering += 2;
        if (Gate(deterministic, "submittedTests")) engineering += 3;
        if (deterministic.Coverage.LinePercent is not null) engineering += 1;
        if (Gate(deterministic, "noNewVulnerabilities") && Gate(deterministic, "noPackageOrBuildChanges") &&
            Gate(deterministic, "noMigrations") && Gate(deterministic, "noBinaries") &&
            Gate(deterministic, "noSecrets") && Gate(deterministic, "diffCheck")) engineering += 4;

        var status = gates.Values.All(x => x) ? "PASS" : "FAIL";
        var semanticGateIds = semantic.Gates.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var gateFailures = gates.Where(x => !x.Value && !semanticGateIds.Contains(x.Key))
            .Select(x => $"Hard gate failed: {x.Key}.");
        return new(
            status,
            acceptance,
            engineering,
            gates,
            semantic.Summary,
            semantic.Strengths,
            semantic.Failures
                .Concat(semantic.Gates.Where(x => !x.Passed).Select(x => $"Semantic gate failed: {x.Id}. {x.Reason}"))
                .Concat(gateFailures)
                .Distinct(StringComparer.Ordinal).ToArray(),
            semantic.Recommendations);
    }

    private static int Points(DeterministicResult result, string group, int points) =>
        result.AcceptanceGroups.GetValueOrDefault(group) ? points : 0;

    private static bool Gate(DeterministicResult result, string gate) => result.HardGates.GetValueOrDefault(gate);
}

internal static class ReportWriter
{
    public static void Write(
        string path,
        RunManifest manifest,
        RunTelemetry telemetry,
        DeterministicResult deterministic,
        SemanticGrade semantic,
        GradeResult grade)
    {
        var failedGates = grade.HardGates.Where(x => !x.Value).Select(x => $"- `{x.Key}`");
        var dimensionRows = semantic.Dimensions.Select(DimensionRow);
        var decision = grade.HardGates.Values.All(x => x)
            ? "All deterministic and semantic gates passed."
            : string.Join(Environment.NewLine, failedGates.Concat(grade.Failures.Select(x => "- " + x)).Distinct(StringComparer.Ordinal));
        var content = $"""
            # TodoApp evaluation: {manifest.RunId}

            **Status:** {grade.Status}  
            **Deterministic attainment:** acceptance {grade.AcceptancePoints}/50; engineering {grade.EngineeringPoints}/20
            **Semantic quality:** {semantic.QualityScore.ToString("F1", CultureInfo.InvariantCulture)}/{semantic.MaximumQualityScore}

            ## Summary

            {grade.Narrative}

            ## Decision

            {decision}

            ## Semantic review

            | Dimension | Weight | Earned |
            | --- | ---: | ---: |
            {string.Join(Environment.NewLine, dimensionRows)}

            ### Findings

            {(semantic.Findings.Count == 0 ? "None." : string.Join(Environment.NewLine, semantic.Findings.Select(FindingLine)))}

            ### Recommended follow-up

            {(grade.Recommendations.Count == 0 ? "None." : string.Join(Environment.NewLine, grade.Recommendations.Select(x => "- " + x)))}

            ## Run details

            - Implementation: `{telemetry.Agent.Provider}` / `{telemetry.Agent.ConfiguredModel}` / `{telemetry.Agent.ConfiguredReasoningEffort}`; exit `{telemetry.Agent.ExitCode}`, {telemetry.Agent.WallClockSeconds:F1}s
            - Semantic grader: `{manifest.SemanticGrader.Provider}` / `{manifest.SemanticGrader.Model}` / `{manifest.SemanticGrader.ReasoningEffort}`
            - Tests: {deterministic.ExistingTests.Passed} existing and {deterministic.PrivateTests.Passed} private passed; {deterministic.ExistingTests.Failed + deterministic.PrivateTests.Failed} failed
            - Coverage: line {Format(deterministic.Coverage.LinePercent)}, branch {Format(deterministic.Coverage.BranchPercent)}; patch: {deterministic.Diff.ChangedFiles.Count} files, +{deterministic.Diff.AddedLines}/-{deterministic.Diff.DeletedLines}
            - Profile: `{manifest.RubricProfileId}` / `{manifest.RubricProfileHash}`

            Full audit detail is retained beside this report in [semantic-grade.json](semantic-grade.json), [deterministic.json](deterministic.json), [telemetry.json](telemetry.json), and [manifest.json](manifest.json).
            """;
        File.WriteAllText(path, content);
    }

    private static string DimensionRow(SemanticDimensionGrade dimension) =>
        $"| `{Cell(dimension.DimensionId)}` — {Cell(dimension.Name)} | {dimension.Weight} | {dimension.EarnedPoints.ToString("0.##", CultureInfo.InvariantCulture)} |";

    private static string FindingLine(SemanticFinding finding) =>
        $"- **{finding.Severity.ToString().ToLowerInvariant()}** `{finding.CriterionId}` at `{finding.File}:{finding.Line}` — {finding.Impact}";

    private static string Format(double? value) => value is null ? "n/a" : $"{value:F1}%";

    private static string Cell(string? value) => value is null ? "n/a" : value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
