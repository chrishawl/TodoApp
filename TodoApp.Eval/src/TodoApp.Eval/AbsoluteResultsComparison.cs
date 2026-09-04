using System.Security.Cryptography;
using System.Text;

namespace TodoApp.Eval;

internal static class AbsoluteResultsComparison
{
    public static string Create(string resultRoot)
    {
        if (!Directory.Exists(resultRoot)) throw new HarnessException($"Results directory does not exist: {resultRoot}");
        var runs = Directory.GetFiles(resultRoot, "manifest.json", SearchOption.AllDirectories)
            .Select(LoadRun)
            .Where(x => x is not null)
            .Cast<ComparableRun>()
            .ToArray();
        if (runs.Length == 0) throw new HarnessException("No completed semantic result runs were found.");

        var compatibility = runs.Select(x => x.Compatibility).Distinct().ToArray();
        if (compatibility.Length != 1)
            throw new HarnessException("Absolute results cannot be compared across different task, base-commit, or review-profile hashes.");
        var dimensionSets = runs.Select(x => string.Join("\n", x.Grade.Dimensions.Select(d => d.DimensionId).Order(StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (dimensionSets.Length != 1)
            throw new HarnessException("Compatible semantic grades do not contain the same dimensions.");

        var groups = runs
            .GroupBy(x => new ImplementationConfiguration(
                x.Manifest.Implementation.Provider.ToString().ToLowerInvariant(),
                x.Manifest.Implementation.Model,
                x.Manifest.Implementation.ReasoningEffort ?? "n/a"))
            .OrderBy(x => x.Key.Provider, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Model, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Effort, StringComparer.Ordinal)
            .ToArray();
        var rows = groups.Select(GroupRow);
        var dimensionIds = runs[0].Grade.Dimensions.Select(x => x.DimensionId).ToArray();
        var dimensionSections = dimensionIds.Select(dimensionId => DimensionSection(dimensionId, groups));
        var descriptive = groups.Any(x => x.Count() == 1)
            ? "At least one model/effort group has a single run. Those comparisons are descriptive only, not general evidence."
            : "These are absolute retained-run summaries; they do not estimate performance outside this compatible sample.";
        return $"""
            # Absolute semantic-results comparison

            Compatibility: task `{compatibility[0].TaskHash}`, base `{compatibility[0].BaseCommitHash}`, profile `{compatibility[0].ProfileHash}`.

            No additional judge was invoked. {descriptive}

            | Implementation model / effort | Samples | Semantic-quality mean | Range |
            | --- | ---: | ---: | ---: |
            {string.Join(Environment.NewLine, rows)}

            ## Per-dimension semantic scores

            {string.Join(Environment.NewLine + Environment.NewLine, dimensionSections)}
            """;
    }

    private static ComparableRun? LoadRun(string manifestPath)
    {
        var directory = Path.GetDirectoryName(manifestPath)!;
        var gradePath = Path.Combine(directory, "semantic-grade.json");
        if (!File.Exists(gradePath)) return null;
        var manifest = Json.Read<RunManifest>(manifestPath);
        var taskHash = manifest.TaskHash;
        if (string.IsNullOrWhiteSpace(taskHash))
        {
            var taskPath = Path.Combine(directory, "task.md");
            if (!File.Exists(taskPath))
                throw new HarnessException($"Run '{manifest.RunId}' has no task hash or retained task.md.");
            taskHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(taskPath)))).ToLowerInvariant();
        }
        if (string.IsNullOrWhiteSpace(manifest.ResolvedCommit) || string.IsNullOrWhiteSpace(manifest.RubricProfileHash))
            throw new HarnessException($"Run '{manifest.RunId}' is missing comparison compatibility hashes.");
        var grade = SemanticGradePersistence.Read(gradePath);
        if (!string.Equals(grade.RubricProfileId, manifest.RubricProfileId, StringComparison.Ordinal) ||
            !string.Equals(grade.RubricProfileHash, manifest.RubricProfileHash, StringComparison.Ordinal))
            throw new HarnessException($"Run '{manifest.RunId}' semantic-grade identity does not match its manifest.");
        return new(
            manifest,
            grade,
            new(taskHash, manifest.ResolvedCommit, manifest.RubricProfileHash));
    }

    private static string GroupRow(IGrouping<ImplementationConfiguration, ComparableRun> group)
    {
        var quality = group.Select(x => x.Grade.QualityScore).ToArray();
        return $"| `{group.Key.Provider}/{group.Key.Model}/{group.Key.Effort}` | {quality.Length} | {Mean(quality):F1} | {quality.Min():F1}–{quality.Max():F1} |";
    }

    private static string DimensionSection(
        string dimensionId,
        IEnumerable<IGrouping<ImplementationConfiguration, ComparableRun>> groups)
    {
        var rows = groups.Select(group =>
        {
            var scores = group.Select(run => run.Grade.Dimensions
                .Single(x => string.Equals(x.DimensionId, dimensionId, StringComparison.Ordinal)).EarnedPoints).ToArray();
            return $"| `{group.Key.Provider}/{group.Key.Model}/{group.Key.Effort}` | {scores.Length} | {Mean(scores):F1} | {scores.Min():F1}–{scores.Max():F1} |";
        });
        return $"""
            ### `{dimensionId}`

            | Implementation model / effort | Samples | Mean | Range |
            | --- | ---: | ---: | ---: |
            {string.Join(Environment.NewLine, rows)}
            """;
    }

    private static decimal Mean(IEnumerable<decimal> values) => values.Average();

    private sealed record ImplementationConfiguration(string Provider, string Model, string Effort);
    private sealed record CompatibilityKey(string TaskHash, string BaseCommitHash, string ProfileHash);
    private sealed record ComparableRun(RunManifest Manifest, SemanticGrade Grade, CompatibilityKey Compatibility);
}
