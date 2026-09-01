using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoApp.Eval;

internal sealed record RubricCriterion(string Id, string Attribute, string Description);

internal sealed record RubricProfile(
    string Id,
    string Version,
    string TaskProfileId,
    int SemanticPoints,
    IReadOnlyDictionary<string, int> VerdictPoints,
    IReadOnlyList<string> ApplicableCriterionIds,
    IReadOnlyList<RubricCriterion> Criteria,
    IReadOnlyList<string> RiskReminders);

internal sealed record ReviewProfile(
    RubricProfile Rubric,
    string RubricJson,
    string Prompt,
    string OutputSchema,
    string Hash)
{
    public static ReviewProfile Load(string controllerRoot, string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || profileId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            profileId.Contains(Path.DirectorySeparatorChar) || profileId.Contains(Path.AltDirectorySeparatorChar))
            throw new HarnessException("Review profile id must be a single valid path segment.");

        var root = Path.Combine(controllerRoot, "review-profiles", profileId);
        var rubricPath = Path.Combine(root, "rubric.json");
        var promptPath = Path.Combine(root, "prompt.md");
        var schemaPath = Path.Combine(root, "output.schema.json");
        foreach (var path in new[] { rubricPath, promptPath, schemaPath })
            if (!File.Exists(path)) throw new HarnessException($"Review profile asset is missing: {path}");

        var rubricJson = File.ReadAllText(rubricPath);
        var prompt = File.ReadAllText(promptPath);
        var schema = File.ReadAllText(schemaPath);
        var rubric = JsonSerializer.Deserialize<RubricProfile>(rubricJson, StrictJsonOptions())
            ?? throw new HarnessException($"Review profile rubric is empty: {rubricPath}");
        Validate(rubric);
        if (!string.Equals(rubric.Id, profileId, StringComparison.Ordinal))
            throw new HarnessException($"Review profile directory '{profileId}' contains rubric '{rubric.Id}'.");
        using (JsonDocument.Parse(schema)) { }
        if (string.IsNullOrWhiteSpace(prompt)) throw new HarnessException($"Review profile prompt is empty: {promptPath}");

        var content = $"rubric.json\n{rubricJson}\nprompt.md\n{prompt}\noutput.schema.json\n{schema}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return new(rubric, rubricJson, prompt, schema, hash);
    }

    private static void Validate(RubricProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Version) ||
            string.IsNullOrWhiteSpace(profile.TaskProfileId))
            throw new HarnessException("Review profile identity fields must be populated.");
        if (profile.SemanticPoints != 30)
            throw new HarnessException("The agentic-v1 semantic rubric must normalize to 30 points.");
        if (profile.VerdictPoints is null || profile.VerdictPoints.Count != 3 ||
            profile.VerdictPoints.GetValueOrDefault("met", -1) != 2 ||
            profile.VerdictPoints.GetValueOrDefault("partial", -1) != 1 ||
            profile.VerdictPoints.GetValueOrDefault("notMet", -1) != 0)
            throw new HarnessException("Review profile verdict points must be met=2, partial=1, and notMet=0.");
        if (profile.Criteria is null || profile.Criteria.Count == 0)
            throw new HarnessException("Review profile must define criteria.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var criterion in profile.Criteria)
        {
            if (string.IsNullOrWhiteSpace(criterion.Id) || string.IsNullOrWhiteSpace(criterion.Attribute) ||
                string.IsNullOrWhiteSpace(criterion.Description))
                throw new HarnessException("Every rubric criterion must have an id, attribute, and description.");
            if (!ids.Add(criterion.Id)) throw new HarnessException($"Duplicate rubric criterion '{criterion.Id}'.");
        }

        var applicable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in profile.ApplicableCriterionIds ?? [])
        {
            if (!ids.Contains(id)) throw new HarnessException($"Applicable rubric criterion '{id}' is not defined.");
            if (!applicable.Add(id)) throw new HarnessException($"Applicable rubric criterion '{id}' is duplicated.");
        }
        if (applicable.Count == 0) throw new HarnessException("Review profile must make at least one criterion applicable.");
    }

    internal static JsonSerializerOptions StrictJsonOptions()
    {
        var options = new JsonSerializerOptions(Json.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Clear();
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}

internal sealed record CompactDiffEvidence(
    IReadOnlyList<string> ChangedFiles,
    int AddedLines,
    int DeletedLines,
    bool HasProductionPatch,
    bool HasSubmittedTests,
    bool DiffCheckPassed,
    bool NoPackageOrBuildChanges,
    bool NoMigrations,
    bool NoBinaries,
    bool NoSecrets);

internal sealed record CompactMechanicalEvidence(
    bool ImplementationSucceeded,
    bool BuildPassed,
    TestSummary ExistingTests,
    bool FormatPassed,
    bool NoNewAnalyzerDiagnostics,
    bool NoNewFormatViolations,
    bool NoNewVulnerabilities,
    CoverageSummary Coverage,
    CompactDiffEvidence Diff)
{
    public static CompactMechanicalEvidence Create(DeterministicResult result, bool implementationSucceeded) => new(
        implementationSucceeded,
        Gate(result, "build"),
        result.ExistingTests,
        Gate(result, "format"),
        Gate(result, "noNewAnalyzerDiagnostics"),
        Gate(result, "noNewFormatViolations"),
        Gate(result, "noNewVulnerabilities"),
        result.Coverage,
        new(
            result.Diff.ChangedFiles.ToArray(),
            result.Diff.AddedLines,
            result.Diff.DeletedLines,
            result.Diff.HasProductionPatch,
            result.Diff.HasSubmittedTests,
            result.Diff.DiffCheckPassed,
            Gate(result, "noPackageOrBuildChanges"),
            Gate(result, "noMigrations"),
            Gate(result, "noBinaries"),
            Gate(result, "noSecrets")));

    private static bool Gate(DeterministicResult result, string name) => result.HardGates.GetValueOrDefault(name);
}

internal sealed record GradingCase(
    string PublicTask,
    string ArchitectureBrief,
    string Worktree,
    string BaseCommit,
    string PatchHash,
    IReadOnlyList<string> ChangedFiles,
    CompactMechanicalEvidence MechanicalEvidence,
    string RubricProfileId,
    string RubricProfileHash)
{
    public static GradingCase Create(
        string worktree,
        string baseCommit,
        string patch,
        DeterministicResult deterministic,
        bool implementationSucceeded,
        ReviewProfile profile) => new(
            TaskDefinition.PublicTask,
            TaskDefinition.ArchitectureBrief,
            Path.GetFullPath(worktree),
            baseCommit,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patch))).ToLowerInvariant(),
            deterministic.Diff.ChangedFiles.ToArray(),
            CompactMechanicalEvidence.Create(deterministic, implementationSucceeded),
            profile.Rubric.Id,
            profile.Hash);
}

internal enum SemanticVerdict { Met, Partial, NotMet, NotApplicable }
internal enum SemanticFindingSeverity { High, Medium, Low }
internal enum EvidenceKind { Source, Command, Mechanical }

internal sealed record EvidenceReference(
    EvidenceKind Kind,
    string Description,
    string? File = null,
    int? Line = null,
    string? Command = null,
    string? MechanicalSignal = null);

internal sealed record CriterionVerdict(
    string CriterionId,
    SemanticVerdict Verdict,
    string Rationale,
    IReadOnlyList<EvidenceReference> Evidence,
    IReadOnlyList<string> FindingIds);

internal sealed record SemanticFinding(
    string Id,
    string CriterionId,
    SemanticFindingSeverity Severity,
    string Trigger,
    string Impact,
    string File,
    int Line,
    IReadOnlyList<EvidenceReference> Evidence);

internal sealed record CoverageReceipt(IReadOnlyList<string> FilesInspected, IReadOnlyList<string> PathsInspected);

internal sealed record SemanticGradeInput(
    IReadOnlyList<CriterionVerdict> Criteria,
    IReadOnlyList<SemanticFinding> Findings,
    CoverageReceipt CoverageReceipt,
    string Summary,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Recommendations);

internal sealed record SemanticCriterionGrade(
    string CriterionId,
    string Attribute,
    SemanticVerdict Verdict,
    int? Points,
    string Rationale,
    IReadOnlyList<EvidenceReference> Evidence,
    IReadOnlyList<string> FindingIds);

internal sealed record SemanticGrade(
    string RubricProfileId,
    string RubricProfileHash,
    int Score,
    int MaximumScore,
    int RawPoints,
    int ApplicableRawPoints,
    IReadOnlyList<SemanticCriterionGrade> Criteria,
    IReadOnlyList<SemanticFinding> Findings,
    CoverageReceipt CoverageReceipt,
    string Summary,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Recommendations);

internal sealed record GraderBudget(int TimeoutSeconds, int MaxToolCalls, int MaxTestCommands)
{
    public static readonly GraderBudget AgenticV1 = new(300, 8, 2);
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}

internal sealed record GraderHealth(bool WithinBudget, IReadOnlyList<string> Warnings)
{
    public static GraderHealth Assess(AgentTelemetry telemetry, GraderBudget budget)
    {
        var warnings = new List<string>();
        if (telemetry.TimedOut || telemetry.WallClockSeconds > budget.TimeoutSeconds)
            warnings.Add($"Grader exceeded its {budget.TimeoutSeconds}s wall-time budget.");
        if (telemetry.ToolCalls is > 0 && telemetry.ToolCalls > budget.MaxToolCalls)
            warnings.Add($"Grader used {telemetry.ToolCalls} tool calls; budget is {budget.MaxToolCalls}.");
        if (telemetry.TestInvocations is > 0 && telemetry.TestInvocations > budget.MaxTestCommands)
            warnings.Add($"Grader used {telemetry.TestInvocations} test commands; budget is {budget.MaxTestCommands}.");
        return new(warnings.Count == 0, warnings);
    }
}

internal interface ISemanticGrader
{
    Task<SemanticGrade> GradeAsync(GradingCase candidate, CancellationToken cancellationToken);
}

internal sealed class SemanticGrading(
    IAgentExecutor agents,
    AgentSpec spec,
    ReviewProfile profile,
    string artifactRoot,
    GraderBudget budget,
    Action<AgentTelemetry>? telemetryAvailable = null) : ISemanticGrader
{
    public async Task<SemanticGrade> GradeAsync(GradingCase candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(candidate.RubricProfileId, profile.Rubric.Id, StringComparison.Ordinal) ||
            !string.Equals(candidate.RubricProfileHash, profile.Hash, StringComparison.Ordinal))
            throw new HarnessException("Grading case review-profile identity does not match the configured grader.");

        Directory.CreateDirectory(artifactRoot);
        Json.Write(Path.Combine(artifactRoot, "grading-case.json"), candidate);
        var prompt = BuildPrompt(candidate);
        var execution = await agents.ExecuteAsync(
            spec,
            candidate.Worktree,
            prompt,
            Path.Combine(artifactRoot, "semantic-grader-agent"),
            budget.Timeout,
            readOnly: true,
            cancellationToken: cancellationToken);
        telemetryAvailable?.Invoke(execution.Telemetry);
        if (execution.Process.TimedOut || execution.Process.ExitCode != 0)
            throw new HarnessException($"{spec.Provider} semantic grader failed (exit {execution.Process.ExitCode}, timeout={execution.Process.TimedOut}).");

        var input = Deserialize(execution.Response);
        var grade = SemanticGradeValidator.ValidateAndScore(profile, candidate, input, execution.Telemetry.ObservedShellCommands);
        Json.Write(Path.Combine(artifactRoot, "semantic-grade.json"), grade);
        return grade;
    }

    private string BuildPrompt(GradingCase candidate) => $$"""
        {{profile.Prompt.Trim()}}

        <evaluator-owned-rubric>
        {{profile.RubricJson}}
        </evaluator-owned-rubric>

        <evaluator-owned-output-schema>
        {{profile.OutputSchema}}
        </evaluator-owned-output-schema>

        <frozen-grading-case>
        {{JsonSerializer.Serialize(candidate with { Worktree = "." }, Json.Options)}}
        </frozen-grading-case>
        """;

    private static SemanticGradeInput Deserialize(string text)
    {
        var value = text.Trim();
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = value.IndexOf('\n');
            var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline) value = value[(firstNewline + 1)..lastFence].Trim();
        }

        try
        {
            return JsonSerializer.Deserialize<SemanticGradeInput>(value, ReviewProfile.StrictJsonOptions())
                ?? throw new JsonException("null result");
        }
        catch (JsonException ex)
        {
            throw new HarnessException("Could not parse semantic grader JSON against the fixed contract.", ex);
        }
    }
}

internal static class SemanticGradeValidator
{
    private static readonly HashSet<string> MechanicalSignals = new(StringComparer.Ordinal)
    {
        "implementationSucceeded",
        "buildPassed",
        "existingTests",
        "formatPassed",
        "noNewAnalyzerDiagnostics",
        "noNewFormatViolations",
        "noNewVulnerabilities",
        "coverage",
        "diff.changedFiles",
        "diff.churn",
        "diff.hasProductionPatch",
        "diff.hasSubmittedTests",
        "diff.diffCheckPassed",
        "diff.noPackageOrBuildChanges",
        "diff.noMigrations",
        "diff.noBinaries",
        "diff.noSecrets"
    };

    public static SemanticGrade ValidateAndScore(
        ReviewProfile profile,
        GradingCase candidate,
        SemanticGradeInput input,
        IReadOnlyList<string>? observedShellCommands = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Criteria is null || input.Findings is null || input.CoverageReceipt is null ||
            input.Strengths is null || input.Failures is null || input.Recommendations is null)
            throw new HarnessException("Semantic grader output is missing required collections.");
        RequireText(input.Summary, "summary");

        var profileCriteria = profile.Rubric.Criteria.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var applicable = profile.Rubric.ApplicableCriterionIds.ToHashSet(StringComparer.Ordinal);
        var verdicts = new Dictionary<string, CriterionVerdict>(StringComparer.Ordinal);
        foreach (var verdict in input.Criteria)
        {
            if (verdict is null || string.IsNullOrWhiteSpace(verdict.CriterionId))
                throw new HarnessException("Semantic grader returned a criterion without an id.");
            if (!profileCriteria.ContainsKey(verdict.CriterionId))
                throw new HarnessException($"Semantic grader returned unknown criterion '{verdict.CriterionId}'.");
            if (!verdicts.TryAdd(verdict.CriterionId, verdict))
                throw new HarnessException($"Semantic grader returned duplicate criterion '{verdict.CriterionId}'.");
            if (applicable.Contains(verdict.CriterionId) == (verdict.Verdict == SemanticVerdict.NotApplicable))
                throw new HarnessException($"Semantic grader contradicted profile-owned applicability for '{verdict.CriterionId}'.");
            RequireText(verdict.Rationale, $"criterion {verdict.CriterionId} rationale");
            if (verdict.Evidence is null || (applicable.Contains(verdict.CriterionId) && verdict.Evidence.Count == 0))
                throw new HarnessException($"Applicable criterion '{verdict.CriterionId}' requires traceable evidence.");
            if (verdict.FindingIds is null || verdict.FindingIds.Any(string.IsNullOrWhiteSpace) ||
                verdict.FindingIds.Count != verdict.FindingIds.Distinct(StringComparer.Ordinal).Count())
                throw new HarnessException($"Criterion '{verdict.CriterionId}' has invalid or duplicate finding ids.");
            ValidateEvidence(verdict.Evidence, candidate.Worktree, $"criterion '{verdict.CriterionId}'", observedShellCommands);
        }

        var missing = profileCriteria.Keys.Where(id => !verdicts.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
            throw new HarnessException("Semantic grader omitted criteria: " + string.Join(", ", missing));

        var findings = new Dictionary<string, SemanticFinding>(StringComparer.Ordinal);
        foreach (var finding in input.Findings)
        {
            if (finding is null || string.IsNullOrWhiteSpace(finding.Id))
                throw new HarnessException("Semantic grader returned a finding without an id.");
            if (!findings.TryAdd(finding.Id, finding))
                throw new HarnessException($"Semantic grader returned duplicate finding '{finding.Id}'.");
            if (!profileCriteria.ContainsKey(finding.CriterionId) || !applicable.Contains(finding.CriterionId))
                throw new HarnessException($"Finding '{finding.Id}' links to an unknown or inapplicable criterion '{finding.CriterionId}'.");
            RequireText(finding.Trigger, $"finding {finding.Id} trigger");
            RequireText(finding.Impact, $"finding {finding.Id} impact");
            ValidateSourceLocation(finding.File, finding.Line, candidate.Worktree, $"finding '{finding.Id}'");
            if (finding.Evidence is null || finding.Evidence.Count == 0)
                throw new HarnessException($"Finding '{finding.Id}' requires evidence.");
            ValidateEvidence(finding.Evidence, candidate.Worktree, $"finding '{finding.Id}'", observedShellCommands);
        }

        foreach (var verdict in verdicts.Values)
        {
            foreach (var findingId in verdict.FindingIds)
            {
                if (!findings.TryGetValue(findingId, out var finding))
                    throw new HarnessException($"Criterion '{verdict.CriterionId}' references unknown finding '{findingId}'.");
                if (!string.Equals(finding.CriterionId, verdict.CriterionId, StringComparison.Ordinal))
                    throw new HarnessException($"Finding '{findingId}' is linked to a different criterion than its verdict.");
            }
        }
        foreach (var finding in findings.Values)
            if (!verdicts[finding.CriterionId].FindingIds.Contains(finding.Id, StringComparer.Ordinal))
                throw new HarnessException($"Finding '{finding.Id}' is not referenced by its linked criterion verdict.");

        ValidateCoverageReceipt(input.CoverageReceipt, candidate);
        ValidateTextList(input.Strengths, "strengths");
        ValidateTextList(input.Failures, "failures");
        ValidateTextList(input.Recommendations, "recommendations");

        var grades = new List<SemanticCriterionGrade>(profile.Rubric.Criteria.Count);
        var rawPoints = 0;
        foreach (var criterion in profile.Rubric.Criteria)
        {
            var returned = verdicts[criterion.Id];
            if (!applicable.Contains(criterion.Id))
            {
                grades.Add(new(criterion.Id, criterion.Attribute, SemanticVerdict.NotApplicable, null,
                    returned.Rationale, returned.Evidence, returned.FindingIds));
                continue;
            }

            var effective = ApplySeverityCap(returned.Verdict,
                findings.Values.Where(x => string.Equals(x.CriterionId, criterion.Id, StringComparison.Ordinal)));
            var points = effective switch
            {
                SemanticVerdict.Met => profile.Rubric.VerdictPoints["met"],
                SemanticVerdict.Partial => profile.Rubric.VerdictPoints["partial"],
                SemanticVerdict.NotMet => profile.Rubric.VerdictPoints["notMet"],
                _ => throw new HarnessException($"Applicable criterion '{criterion.Id}' cannot be notApplicable.")
            };
            rawPoints += points;
            grades.Add(new(criterion.Id, criterion.Attribute, effective, points,
                returned.Rationale, returned.Evidence, returned.FindingIds));
        }

        var applicableRawPoints = applicable.Count * profile.Rubric.VerdictPoints["met"];
        var score = (int)Math.Round(
            (decimal)rawPoints * profile.Rubric.SemanticPoints / applicableRawPoints,
            MidpointRounding.AwayFromZero);
        return new(
            profile.Rubric.Id,
            profile.Hash,
            score,
            profile.Rubric.SemanticPoints,
            rawPoints,
            applicableRawPoints,
            grades,
            input.Findings.ToArray(),
            input.CoverageReceipt,
            input.Summary,
            input.Strengths.ToArray(),
            input.Failures.ToArray(),
            input.Recommendations.ToArray());
    }

    private static SemanticVerdict ApplySeverityCap(SemanticVerdict verdict, IEnumerable<SemanticFinding> findings)
    {
        var severities = findings.Select(x => x.Severity).ToArray();
        if (severities.Contains(SemanticFindingSeverity.High)) return SemanticVerdict.NotMet;
        if (severities.Contains(SemanticFindingSeverity.Medium) && verdict == SemanticVerdict.Met) return SemanticVerdict.Partial;
        return verdict;
    }

    private static void ValidateCoverageReceipt(CoverageReceipt receipt, GradingCase candidate)
    {
        if (receipt.FilesInspected is null || receipt.FilesInspected.Count == 0 || receipt.PathsInspected is null)
            throw new HarnessException("Semantic grader must return a non-empty coverage receipt.");
        if (receipt.FilesInspected.Count != receipt.FilesInspected.Distinct(StringComparer.Ordinal).Count() ||
            receipt.PathsInspected.Count != receipt.PathsInspected.Distinct(StringComparer.Ordinal).Count())
            throw new HarnessException("Semantic grader coverage receipt contains duplicate paths.");

        var changedFiles = candidate.ChangedFiles.Select(NormalizeRelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var file in receipt.FilesInspected)
        {
            var full = ResolvePath(candidate.Worktree, file, "coverage receipt file");
            if (!File.Exists(full) && !changedFiles.Contains(NormalizeRelativePath(file)))
                throw new HarnessException($"Coverage receipt file does not exist and is not a deleted changed file: {file}");
        }
        foreach (var path in receipt.PathsInspected)
        {
            var full = ResolvePath(candidate.Worktree, path, "coverage receipt path");
            if (!File.Exists(full) && !Directory.Exists(full))
                throw new HarnessException($"Coverage receipt path does not exist: {path}");
        }

        var inspected = receipt.FilesInspected.Select(NormalizeRelativePath).ToHashSet(StringComparer.Ordinal);
        var missingChangedFiles = candidate.ChangedFiles.Select(NormalizeRelativePath).Where(x => !inspected.Contains(x)).ToArray();
        if (missingChangedFiles.Length > 0)
            throw new HarnessException("Coverage receipt omitted changed files: " + string.Join(", ", missingChangedFiles));
    }

    private static void ValidateEvidence(
        IEnumerable<EvidenceReference> evidence,
        string worktree,
        string owner,
        IReadOnlyList<string>? observedShellCommands)
    {
        foreach (var item in evidence)
        {
            if (item is null) throw new HarnessException($"{owner} contains null evidence.");
            RequireText(item.Description, $"{owner} evidence description");
            switch (item.Kind)
            {
                case EvidenceKind.Source:
                    if (item.Command is not null || item.MechanicalSignal is not null)
                        throw new HarnessException($"{owner} source evidence contains fields for another evidence kind.");
                    ValidateSourceLocation(item.File, item.Line, worktree, owner);
                    break;
                case EvidenceKind.Command:
                    if (string.IsNullOrWhiteSpace(item.Command) || item.File is not null || item.Line is not null || item.MechanicalSignal is not null)
                        throw new HarnessException($"{owner} command evidence must contain only an exact command and description.");
                    if (observedShellCommands is null || !observedShellCommands.Any(command =>
                            string.Equals(command.Trim(), item.Command.Trim(), StringComparison.Ordinal)))
                        throw new HarnessException($"{owner} cites a command that was not observed in the grader trace.");
                    break;
                case EvidenceKind.Mechanical:
                    if (item.File is not null || item.Line is not null || item.Command is not null ||
                        item.MechanicalSignal is null || !MechanicalSignals.Contains(item.MechanicalSignal))
                        throw new HarnessException($"{owner} references an invalid compact mechanical signal.");
                    break;
                default:
                    throw new HarnessException($"{owner} contains an unknown evidence kind.");
            }
        }
    }

    private static void ValidateSourceLocation(string? file, int? line, string worktree, string owner)
    {
        if (string.IsNullOrWhiteSpace(file) || line is null or < 1)
            throw new HarnessException($"{owner} requires a repository-relative file and positive line.");
        var full = ResolvePath(worktree, file, owner);
        if (!File.Exists(full)) throw new HarnessException($"{owner} references missing source file '{file}'.");
        var lineCount = File.ReadLines(full).Count();
        if (line > lineCount) throw new HarnessException($"{owner} references line {line} beyond '{file}' ({lineCount} lines).");
    }

    private static string ResolvePath(string worktree, string relativePath, string owner)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new HarnessException($"{owner} path must be repository-relative.");
        var root = Path.GetFullPath(worktree).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(worktree, relativePath));
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new HarnessException($"{owner} path escapes the frozen worktree: {relativePath}");
        var worktreeRoot = root.TrimEnd(Path.DirectorySeparatorChar);
        if (IsSymbolicLink(worktreeRoot))
            throw new HarnessException($"{owner} cannot resolve through a symlinked worktree root.");
        var current = worktreeRoot;
        var segments = Path.GetRelativePath(worktreeRoot, full)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (IsSymbolicLink(current))
                throw new HarnessException($"{owner} path resolves through a symlink: {relativePath}");
        }
        return full;
    }

    private static bool IsSymbolicLink(string path) =>
        new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void ValidateTextList(IEnumerable<string> values, string name)
    {
        if (values.Any(string.IsNullOrWhiteSpace)) throw new HarnessException($"Semantic grader {name} contains an empty item.");
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new HarnessException($"Semantic grader {name} must not be empty.");
    }
}
