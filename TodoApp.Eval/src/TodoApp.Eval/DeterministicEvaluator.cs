using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TodoApp.Eval;

internal sealed class DeterministicEvaluator(ProcessRunner processes, GitWorkspace git, string controllerRoot)
{
    public async Task<DeterministicResult> EvaluateAsync(
        string worktree,
        string baseCommit,
        string artifactRoot,
        TimeSpan timeout,
        DeterministicResult? baseline,
        bool includePrivateTests)
    {
        Directory.CreateDirectory(artifactRoot);
        var checks = new List<CheckResult>();
        var solution = Path.Combine(worktree, "TodoApp.sln");

        var build = await Run("build", Path.Combine(artifactRoot, "build.log"), new("dotnet",
            ["build", solution, "--no-restore", "--nologo", "-p:EnableNETAnalyzers=true", "-p:AnalysisLevel=latest-recommended", "-p:AnalysisMode=All"], worktree), timeout);
        checks.Add(build.Check);
        var diagnostics = ParseDiagnostics(build.Result.Stdout + build.Result.Stderr, worktree);
        Json.Write(Path.Combine(artifactRoot, "analyzers.json"), diagnostics);

        var format = await Run("format", Path.Combine(artifactRoot, "format.log"), new("dotnet",
            ["format", solution, "--verify-no-changes", "--no-restore", "--verbosity", "diagnostic"], worktree,
            Environment: new Dictionary<string, string?> { ["NuGetAudit"] = "false" }), timeout);
        checks.Add(format.Check);
        var formatFindings = ParseFormat(format.Result.Stdout + format.Result.Stderr, worktree);
        Json.Write(Path.Combine(artifactRoot, "format.json"), formatFindings);

        var existingTrx = Path.Combine(artifactRoot, "existing-tests.trx");
        var existing = await Run("existing-tests", Path.Combine(artifactRoot, "existing-tests.log"), new("dotnet",
            ["test", solution, "--no-restore", "--nologo", "--logger", $"trx;LogFileName={existingTrx}"], worktree), timeout);
        checks.Add(existing.Check);
        var existingSummary = ParseTrx(existingTrx);

        var coverageDirectory = Path.Combine(artifactRoot, "coverage");
        Directory.CreateDirectory(coverageDirectory);
        var coverageRun = await Run("coverage", Path.Combine(artifactRoot, "coverage.log"), new("dotnet",
            ["test", solution, "--no-restore", "--nologo", "--collect", "XPlat Code Coverage", "--results-directory", coverageDirectory], worktree), timeout);
        checks.Add(coverageRun.Check);
        var coverage = ParseCoverage(coverageDirectory);
        Json.Write(Path.Combine(artifactRoot, "coverage.json"), coverage);

        var vulnerability = await Run("vulnerabilities", Path.Combine(artifactRoot, "vulnerabilities.json"), new("dotnet",
            ["list", solution, "package", "--vulnerable", "--include-transitive", "--format", "json"], worktree), timeout);
        checks.Add(vulnerability.Check);
        var vulnerabilities = ParseVulnerabilities(vulnerability.Result.Stdout);

        var patch = await git.CapturePatchAsync(worktree, baseCommit);
        var inventory = await git.InventoryAsync(worktree, baseCommit, patch);
        Json.Write(Path.Combine(artifactRoot, "diff-inventory.json"), inventory);

        var privateSummary = new TestSummary(0, 0, 0, 0, false);
        var acceptance = new Dictionary<string, bool>
        {
            ["authenticationIsolation"] = false,
            ["filtering"] = false,
            ["paginationContract"] = false,
            ["validationRegression"] = false
        };
        if (includePrivateTests && build.Check.Passed)
        {
            var privateResult = await RunPrivateTests(worktree, artifactRoot, timeout);
            checks.Add(privateResult.Check);
            privateSummary = ParseTrx(privateResult.TrxPath);
            acceptance = ParseAcceptanceGroups(privateResult.TrxPath);
        }

        var newDiagnostics = baseline is null ? Array.Empty<DiagnosticFinding>() : MultisetDelta(diagnostics, baseline.AnalyzerFindings, FindingKey).ToArray();
        var newFormat = baseline is null ? Array.Empty<FormatFinding>() : MultisetDelta(formatFindings, baseline.FormatFindings, FormatKey).ToArray();
        var newVulnerabilities = baseline is null ? Array.Empty<VulnerabilityFinding>() : MultisetDelta(vulnerabilities, baseline.Vulnerabilities, VulnerabilityKey).ToArray();
        Json.Write(Path.Combine(artifactRoot, "analyzer-delta.json"), newDiagnostics);
        Json.Write(Path.Combine(artifactRoot, "format-delta.json"), newFormat);
        Json.Write(Path.Combine(artifactRoot, "vulnerability-delta.json"), newVulnerabilities);

        var gates = new Dictionary<string, bool>
        {
            ["agentProducedProductionPatch"] = !includePrivateTests || inventory.HasProductionPatch,
            ["build"] = build.Check.Passed,
            ["existingTests"] = IsExistingTestRunClean(existing.Check, existingSummary),
            ["format"] = IsFormatClean(format.Check, formatFindings),
            ["privateAcceptanceTests"] = !includePrivateTests || (privateSummary.Discovered && privateSummary.Failed == 0 && acceptance.Values.All(x => x)),
            ["noNewAnalyzerDiagnostics"] = baseline is null || newDiagnostics.Length == 0,
            ["noNewFormatViolations"] = baseline is null || newFormat.Length == 0,
            ["noNewVulnerabilities"] = baseline is null || newVulnerabilities.Length == 0,
            ["noPackageOrBuildChanges"] = !includePrivateTests || inventory.PackageOrProjectFiles.Count == 0,
            ["noMigrations"] = !includePrivateTests || inventory.Migrations.Count == 0,
            ["noBinaries"] = !includePrivateTests || inventory.BinaryFiles.Count == 0,
            ["noSecrets"] = !includePrivateTests || inventory.SecretFindings.Count == 0,
            ["diffCheck"] = inventory.DiffCheckPassed,
            ["submittedTests"] = !includePrivateTests || (inventory.HasSubmittedTests && baseline is not null &&
                existingSummary.Passed + existingSummary.Failed + existingSummary.Skipped > baseline.ExistingTests.Passed + baseline.ExistingTests.Failed + baseline.ExistingTests.Skipped),
            ["noCriticalSecurityFinding"] = true
        };
        var result = new DeterministicResult(checks, diagnostics, formatFindings, vulnerabilities, existingSummary,
            privateSummary, coverage, inventory, acceptance, gates, gates.Values.All(x => x));
        Json.Write(Path.Combine(artifactRoot, "deterministic.json"), result);
        return result;
    }

    private async Task<(CheckResult Check, string TrxPath)> RunPrivateTests(string worktree, string artifactRoot, TimeSpan timeout)
    {
        var source = Path.Combine(controllerRoot, "private-tests", "TodoApp.Eval.PrivateTests");
        if (!Directory.Exists(source)) throw new HarnessException($"Private test assets are missing at {source}.");
        var privateRoot = Path.Combine(artifactRoot, "private-tests");
        CopyDirectory(source, privateRoot);
        var project = Path.Combine(privateRoot, "TodoApp.Eval.PrivateTests.csproj");
        var candidate = Path.Combine(worktree, "Todo.Api", "Todo.Api.csproj");
        var trx = Path.Combine(artifactRoot, "private-tests.trx");
        var run = await Run("private-tests", Path.Combine(artifactRoot, "private-tests.log"), new("dotnet",
            ["test", project, "--nologo", "--logger", $"trx;LogFileName={trx}", $"-p:CandidateApiProject={candidate}"], privateRoot), timeout);
        return (run.Check, trx);
    }

    private async Task<(CheckResult Check, ProcessResult Result)> Run(string name, string artifact, ProcessSpec spec, TimeSpan timeout)
    {
        var result = await processes.RunAsync(spec, timeout);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, result.Stdout + Environment.NewLine + result.Stderr);
        return (new(name, result.ExitCode == 0 && !result.TimedOut, result.ExitCode, result.TimedOut, result.Duration.TotalSeconds, Path.GetFileName(artifact)), result);
    }

    private static DiagnosticFinding[] ParseDiagnostics(string text, string root)
    {
        var regex = new Regex(@"^(?<file>.+?)(?:\(\d+(?:,\d+)?\))?: (?<severity>warning|error) (?<rule>[A-Za-z]+\d+): (?<message>.*?)(?: \[.+\])?$", RegexOptions.Multiline);
        return regex.Matches(text).Select(m => new DiagnosticFinding(m.Groups["severity"].Value.ToLowerInvariant(), m.Groups["rule"].Value,
            Relative(m.Groups["file"].Value.Trim(), root), m.Groups["message"].Value.Trim())).Distinct().OrderBy(FindingKey).ToArray();
    }

    private static FormatFinding[] ParseFormat(string text, string root)
    {
        var regex = new Regex(@"^(?<file>.+?)\(\d+,\d+\):\s*(?:error|warning)\s+(?<diag>[A-Za-z0-9_-]+):\s*(?<message>.*?)(?:\s+\[.+\])?$", RegexOptions.Multiline);
        return regex.Matches(text).Select(m => new FormatFinding(Relative(m.Groups["file"].Value.Trim(), root), m.Groups["diag"].Value, m.Groups["message"].Value.Trim()))
            .Distinct().OrderBy(FormatKey).ToArray();
    }

    private static VulnerabilityFinding[] ParseVulnerabilities(string json)
    {
        var results = new List<VulnerabilityFinding>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            Walk(doc.RootElement, null, null, results);
        }
        catch (JsonException) { }
        return results.Distinct().OrderBy(VulnerabilityKey).ToArray();
    }

    private static void Walk(JsonElement e, string? package, string? version, List<VulnerabilityFinding> results)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var currentPackage = GetString(e, "id") ?? package;
            var currentVersion = GetString(e, "resolvedVersion") ?? version;
            if (e.TryGetProperty("advisoryurl", out var advisory) || e.TryGetProperty("advisoryUrl", out advisory))
                results.Add(new(currentPackage ?? "unknown", currentVersion ?? "unknown", advisory.GetString() ?? "unknown", GetString(e, "severity") ?? "unknown"));
            foreach (var p in e.EnumerateObject()) Walk(p.Value, currentPackage, currentVersion, results);
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x, package, version, results);
    }

    private static string? GetString(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    internal static TestSummary ParseTrx(string path)
    {
        if (!File.Exists(path)) return new(0, 1, 0, 0, false);
        var doc = XDocument.Load(path); var counters = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Counters");
        var times = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Times");
        return new(Int(counters, "passed"), Int(counters, "failed") + Int(counters, "error"), Int(counters, "notExecuted"),
            Duration(times?.Attribute("finish")?.Value, times?.Attribute("start")?.Value), Int(counters, "total") > 0);
    }

    internal static Dictionary<string, bool> ParseAcceptanceGroups(string path)
    {
        var groups = new Dictionary<string, bool>
        {
            ["authenticationIsolation"] = false,
            ["filtering"] = false,
            ["paginationContract"] = false,
            ["validationRegression"] = false
        };
        if (!File.Exists(path)) return groups;
        var doc = XDocument.Load(path);
        var results = doc.Descendants().Where(x => x.Name.LocalName == "UnitTestResult").ToArray();
        foreach (var key in groups.Keys.ToArray())
        {
            var matching = results.Where(r =>
            {
                var name = r.Attribute("testName")?.Value ?? "";
                return name.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("." + key + "_", StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            groups[key] = matching.Length > 0 && matching.All(r => string.Equals(r.Attribute("outcome")?.Value, "Passed", StringComparison.OrdinalIgnoreCase));
        }
        return groups;
    }

    private static CoverageSummary ParseCoverage(string directory)
    {
        var file = Directory.Exists(directory) ? Directory.GetFiles(directory, "coverage.cobertura.xml", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (file is null) return new(null, null);
        var root = XDocument.Load(file).Root;
        return new(Percent(root?.Attribute("line-rate")?.Value), Percent(root?.Attribute("branch-rate")?.Value));
    }

    internal static IEnumerable<T> MultisetDelta<T>(IEnumerable<T> candidate, IEnumerable<T> baseline, Func<T, string> key)
    {
        var debt = baseline.GroupBy(key).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var finding in candidate)
        {
            var k = key(finding);
            if (debt.TryGetValue(k, out var count) && count > 0) debt[k] = count - 1;
            else yield return finding;
        }
    }

    internal static string FindingKey(DiagnosticFinding f) => $"{f.Severity}|{f.Rule}|{f.File}|{f.Message}";
    internal static string FormatKey(FormatFinding f) => $"{f.File}|{f.Diagnostic}|{f.Message}";
    internal static string VulnerabilityKey(VulnerabilityFinding f) => $"{f.Package}|{f.Version}|{f.Advisory}|{f.Severity}";
    internal static bool IsExistingTestRunClean(CheckResult check, TestSummary summary) =>
        check.Passed && summary.Discovered && summary.Failed == 0 && summary.Skipped == 0;
    internal static bool IsFormatClean(CheckResult check, IReadOnlyList<FormatFinding> findings) =>
        check.Passed && findings.Count == 0;
    private static string Relative(string file, string root) => Path.IsPathRooted(file) ? Path.GetRelativePath(root, file) : file;
    private static int Int(XElement? e, string name) => int.TryParse(e?.Attribute(name)?.Value, out var x) ? x : 0;
    private static double Duration(string? finish, string? start) => DateTimeOffset.TryParse(finish, out var f) && DateTimeOffset.TryParse(start, out var s) ? (f - s).TotalSeconds : 0;
    private static double? Percent(string? rate) => double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x * 100 : null;
    internal static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(directory.Replace(source, target, StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(source, target, StringComparison.Ordinal), overwrite: true);
    }
}
