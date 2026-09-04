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
            acceptance + engineering + semantic.CompositePoints,
            acceptance,
            engineering,
            semantic.CompositePoints,
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
        var failed = grade.HardGates.Where(x => !x.Value).Select(x => $"- `{x.Key}`");
        var roleTelemetry = new List<(string Role, AgentTelemetry Telemetry)> { ("Implementation", telemetry.Agent) };
        if (telemetry.SemanticGrader is not null) roleTelemetry.Add(("Semantic grader", telemetry.SemanticGrader));
        var agentRows = roleTelemetry.Select(x => AgentRow(x.Role, x.Telemetry)).ToList();
        if (telemetry.Aggregate is not null) agentRows.Add(AggregateRow(telemetry.Aggregate));
        var skillRows = roleTelemetry.Select(x => SkillRow(x.Role, x.Telemetry));
        var dimensionRows = semantic.Dimensions.Select(DimensionRow);
        var rubricRows = semantic.Criteria.Select(CriterionRow);
        var semanticGateRows = semantic.Gates.Select(SemanticGateRow);
        var health = manifest.GraderHealth;
        var content = $"""
            # TodoApp evaluation: {manifest.RunId}

            **Status:** {grade.Status}  
            **Score:** {grade.Score}/100 (acceptance {grade.AcceptancePoints}/50, engineering {grade.EngineeringPoints}/20, semantic {grade.SemanticPoints}/30)
            **Semantic quality:** {semantic.QualityScore.ToString("F1", CultureInfo.InvariantCulture)}/{semantic.MaximumQualityScore} (contributes {semantic.CompositePoints}/{semantic.MaximumCompositePoints} to the composite)

            ## Summary

            {grade.Narrative}

            ## Hard gates

            {(failed.Any() ? string.Join(Environment.NewLine, failed) : "All hard gates passed.")}

            ## Execution

            - Baseline ready: `{manifest.BaselineReadiness?.Ready}` ({manifest.BaselineReadiness?.ExistingTests.Passed}/{manifest.BaselineReadiness?.ExpectedTestCount} expected tests passed)
            - Implementation provider/configured model: `{telemetry.Agent.Provider}` / `{telemetry.Agent.ConfiguredModel}`
            - Implementation exit/timeout: `{telemetry.Agent.ExitCode}` / `{telemetry.Agent.TimedOut}`
            - Implementation wall clock: `{telemetry.Agent.WallClockSeconds:F1}s`
            - Semantic profile: `{manifest.RubricProfileId}` / `{manifest.RubricProfileHash}`
            - Semantic grader: `{manifest.SemanticGrader.Provider}` / `{manifest.SemanticGrader.Model}` / `{manifest.SemanticGrader.ReasoningEffort}`
            - Grader budget: one invocation, {manifest.GraderBudget.TimeoutSeconds}s, {manifest.GraderBudget.MaxToolCalls} tool calls, {manifest.GraderBudget.MaxTestCommands} focused test commands
            - Grader cost inputs: {manifest.GraderCostInputs?.ModelCalls ?? (telemetry.SemanticGrader is null ? 0 : 1)} model call; token counts are recorded in the telemetry table and manifest
            - Grader health: `{(health is null ? "unknown" : health.WithinBudget ? "within budget" : "warning")}`
            - Patch: {deterministic.Diff.ChangedFiles.Count} files, +{deterministic.Diff.AddedLines}/-{deterministic.Diff.DeletedLines}
            - Existing tests: {deterministic.ExistingTests.Passed} passed, {deterministic.ExistingTests.Failed} failed
            - Private tests: {deterministic.PrivateTests.Passed} passed, {deterministic.PrivateTests.Failed} failed
            - Coverage: line {Format(deterministic.Coverage.LinePercent)}, branch {Format(deterministic.Coverage.BranchPercent)}

            {(health is { Warnings.Count: > 0 } ? string.Join(Environment.NewLine, health.Warnings.Select(x => $"- Grader health warning: {x}")) : "")}

            ## Agent telemetry

            | Role | Configured model | Reported model | Reasoning effort | CLI version | Turns | Tool calls | Shell commands | Failures | Wall time | Input | Cached input | Output | Reasoning | Input + output |
            | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
            {string.Join(Environment.NewLine, agentRows)}

            | Role | Host skills | Provider usage | Observed skill names |
            | --- | --- | --- | --- |
            {string.Join(Environment.NewLine, skillRows)}

            Host skills, configuration, and plugins are excluded by container isolation. Current Codex JSONL does not expose skill invocation, so observed skill names are unknown—not zero.

            ## Semantic rubric

            ### Dimension subtotals

            | Dimension | Weight | Earned |
            | --- | ---: | ---: |
            {string.Join(Environment.NewLine, dimensionRows)}

            ### Criteria

            | Criterion | Weight | Level | Earned | Rationale | Evidence | Next-level gap |
            | --- | ---: | --- | ---: | --- | --- | --- |
            {string.Join(Environment.NewLine, rubricRows)}

            Semantic quality: {semantic.QualityScore.ToString("F1", CultureInfo.InvariantCulture)}/{semantic.MaximumQualityScore}. Composite contribution: {semantic.CompositePoints}/{semantic.MaximumCompositePoints}. The evaluator calculates both values from criterion weights and effective levels.

            ### Semantic gates

            | Gate | Result | Reason |
            | --- | --- | --- |
            {string.Join(Environment.NewLine, semanticGateRows)}

            ## Findings

            {(semantic.Findings.Count == 0 ? "None." : string.Join(Environment.NewLine, semantic.Findings.Select(FindingLine)))}

            ## Coverage receipt

            - Files inspected: {string.Join(", ", semantic.CoverageReceipt.FilesInspected.Select(x => $"`{x}`"))}
            - Paths inspected: {(semantic.CoverageReceipt.PathsInspected.Count == 0 ? "none" : string.Join(", ", semantic.CoverageReceipt.PathsInspected.Select(x => $"`{x}`")))}

            ## Strengths

            {(grade.Strengths.Count == 0 ? "None." : string.Join(Environment.NewLine, grade.Strengths.Select(x => "- " + x)))}

            ## Failures

            {(grade.Failures.Count == 0 ? "None." : string.Join(Environment.NewLine, grade.Failures.Select(x => "- " + x)))}

            ## Recommendations

            {(grade.Recommendations.Count == 0 ? "None." : string.Join(Environment.NewLine, grade.Recommendations.Select(x => "- " + x)))}
            """;
        File.WriteAllText(path, content);
    }

    private static string DimensionRow(SemanticDimensionGrade dimension) =>
        $"| `{Cell(dimension.DimensionId)}` — {Cell(dimension.Name)} | {dimension.Weight} | {dimension.EarnedPoints.ToString("0.##", CultureInfo.InvariantCulture)} |";

    private static string CriterionRow(SemanticCriterionGrade criterion)
    {
        var level = criterion.Level == criterion.ReportedLevel
            ? Level(criterion.Level)
            : $"{Level(criterion.Level)} (reported {Level(criterion.ReportedLevel)}, capped)";
        return $"| `{Cell(criterion.CriterionId)}` | {criterion.Weight} | `{level}` | {criterion.EarnedPoints.ToString("0.##", CultureInfo.InvariantCulture)} | {Cell(criterion.Rationale)} | {Cell(string.Join("; ", criterion.Evidence.Select(EvidenceText)))} | {Cell(criterion.NextLevelGap)} |";
    }

    private static string SemanticGateRow(SemanticGateResult gate) =>
        $"| `{Cell(gate.Id)}` | `{(gate.Passed ? "PASS" : "FAIL")}` | {Cell(gate.Reason)} |";

    private static string FindingLine(SemanticFinding finding) =>
        $"- **{finding.Severity.ToString().ToLowerInvariant()}** `{finding.CriterionId}` at `{finding.File}:{finding.Line}` — " +
        $"Trigger: {finding.Trigger} Impact: {finding.Impact} Evidence: {string.Join("; ", finding.Evidence.Select(EvidenceText))}";

    private static string EvidenceText(EvidenceReference evidence) => evidence.Kind switch
    {
        EvidenceKind.Source => $"{evidence.File}:{evidence.Line} ({evidence.Description})",
        EvidenceKind.Command => $"{evidence.Command} ({evidence.Description})",
        EvidenceKind.Mechanical => $"{evidence.MechanicalSignal} ({evidence.Description})",
        _ => evidence.Description
    };

    private static string Format(double? value) => value is null ? "n/a" : $"{value:F1}%";
    private static string Level(SemanticLevel value) => value == SemanticLevel.AdequateWithGaps
        ? "adequate-with-gaps"
        : value.ToString().ToLowerInvariant();

    private static string AgentRow(string role, AgentTelemetry telemetry) =>
        $"| {Cell(role)} | {Cell(telemetry.ConfiguredModel)} | {Cell(telemetry.ReportedModel)} | {Cell(telemetry.ConfiguredReasoningEffort)} | {Cell(telemetry.CliVersion)} | {Number(telemetry.Turns)} | {Number(telemetry.ToolCalls)} | {Number(telemetry.ShellCommands)} | {Number(telemetry.FailedTools)} | {telemetry.WallClockSeconds:F1}s | {Number(telemetry.Tokens.Input)} | {Number(telemetry.Tokens.CachedInput)} | {Number(telemetry.Tokens.Output)} | {Number(telemetry.Tokens.Reasoning)} | {Number(telemetry.Tokens.InputOutput)} |";

    private static string AggregateRow(AggregateAgentTelemetry telemetry) =>
        $"| Total | n/a | n/a | n/a | n/a | {Number(telemetry.Turns)} | {Number(telemetry.ToolCalls)} | {Number(telemetry.ShellCommands)} | {Number(telemetry.FailedTools)} | {telemetry.WallClockSeconds:F1}s | {Number(telemetry.Tokens.Input)} | {Number(telemetry.Tokens.CachedInput)} | {Number(telemetry.Tokens.Output)} | {Number(telemetry.Tokens.Reasoning)} | {Number(telemetry.Tokens.InputOutput)} |";

    private static string SkillRow(string role, AgentTelemetry telemetry) =>
        $"| {Cell(role)} | {Availability(telemetry.HostSkillsAvailable)} | {Exposure(telemetry.ProviderUsageExposed)} | {Observed(telemetry.ObservedSkillNames)} |";

    private static string Cell(string? value) => value is null ? "n/a" : value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
    private static string Availability(bool? value) => value switch { true => "available", false => "unavailable", null => "n/a" };
    private static string Exposure(bool? value) => value switch { true => "exposed", false => "not exposed", null => "n/a" };
    private static string Observed(IReadOnlyList<string>? values) => values is null ? "unknown" : values.Count == 0 ? "none" : Cell(string.Join(", ", values));
}
