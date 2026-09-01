using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TodoApp.Eval;

internal sealed class GitWorkspace(ProcessRunner processes)
{
    private static readonly HashSet<string> ProductionExtensions =
        new([".cs", ".fs", ".vb", ".cshtml", ".razor", ".json", ".xml"], StringComparer.OrdinalIgnoreCase);

    public async Task<string> ResolveCommitAsync(string repository, string commit)
    {
        var result = await Git(repository, ["rev-parse", "--verify", commit + "^{commit}"]);
        if (result.ExitCode != 0) throw new HarnessException($"Base commit '{commit}' does not resolve in {repository}: {result.Stderr}");
        return result.Stdout.Trim();
    }

    public async Task CreateAsync(string repository, string worktree, string commit, string? branch)
    {
        if (Directory.Exists(worktree)) throw new HarnessException($"Worktree path already exists: {worktree}");
        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        var args = branch is null
            ? new[] { "worktree", "add", "--detach", worktree, commit }
            : new[] { "worktree", "add", "-b", branch, worktree, commit };
        var result = await Git(repository, args);
        if (result.ExitCode != 0) throw new HarnessException($"Could not create worktree: {result.Stderr}");
    }

    public async Task RemoveAsync(string repository, string worktree, string? branch)
    {
        var remove = await Git(repository, ["worktree", "remove", "--force", worktree]);
        if (remove.ExitCode != 0) throw new HarnessException($"Could not remove worktree: {remove.Stderr}");
        if (branch is not null) await Git(repository, ["branch", "-D", branch]);
    }

    public async Task<string> CapturePatchAsync(string worktree, string baseCommit)
    {
        var tracked = await Git(worktree, ["diff", "--binary", "--full-index", baseCommit, "--"]);
        var status = await Git(worktree, ["status", "--porcelain=v1", "-z", "--untracked-files=all"]);
        var builder = new StringBuilder(tracked.Stdout);
        var entries = status.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries.Where(e => e.StartsWith("?? ", StringComparison.Ordinal)))
        {
            var relative = entry[3..];
            var full = Path.Combine(worktree, relative);
            if (Directory.Exists(full)) continue;
            var diff = await processes.RunAsync(new("git", ["diff", "--no-index", "--binary", "--full-index", "--", "/dev/null", full], worktree), TimeSpan.FromMinutes(2));
            if (diff.ExitCode is not (0 or 1)) throw new HarnessException($"Failed to capture untracked file {relative}: {diff.Stderr}");
            builder.Append(diff.Stdout.Replace("b" + full, "b/" + relative, StringComparison.Ordinal)
                                          .Replace("a/dev/null", "a/dev/null", StringComparison.Ordinal));
        }
        return builder.ToString();
    }

    public async Task<DiffInventory> InventoryAsync(string worktree, string baseCommit, string patch)
    {
        var nameStatus = await Git(worktree, ["diff", "--name-status", baseCommit, "--"]);
        var untracked = await Git(worktree, ["ls-files", "--others", "--exclude-standard"]);
        var files = nameStatus.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('\t').Last())
            .Concat(untracked.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal).Order().ToArray();
        var numstat = await Git(worktree, ["diff", "--numstat", baseCommit, "--"]);
        var added = 0; var deleted = 0;
        foreach (var row in numstat.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = row.Split('\t');
            if (p.Length >= 2 && int.TryParse(p[0], out var a)) added += a;
            if (p.Length >= 2 && int.TryParse(p[1], out var d)) deleted += d;
        }
        foreach (var file in untracked.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try { added += File.ReadLines(Path.Combine(worktree, file)).Count(); } catch (Exception) when (IsBinary(Path.Combine(worktree, file))) { }
        }
        var check = await Git(worktree, ["diff", "--check", baseCommit, "--"]);
        var commits = await Git(worktree, ["rev-list", "--count", baseCommit + "..HEAD"]);
        var binaries = files.Where(f => IsBinary(Path.Combine(worktree, f))).ToArray();
        var symlinks = files.Where(f => IsSymlink(Path.Combine(worktree, f))).ToArray();
        var testFiles = files.Where(IsTest).ToHashSet(StringComparer.Ordinal);
        var production = files.Where(f => IsProduction(f) && !testFiles.Contains(f)).ToHashSet(StringComparer.Ordinal);
        var productionLines = CountAddedLines(patch, production);
        var testLines = CountAddedLines(patch, testFiles);
        return new(files, added, deleted, productionLines, testLines,
            int.TryParse(commits.Stdout.Trim(), out var c) ? c : 0,
            files.Where(IsGenerated).ToArray(), binaries, symlinks,
            files.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                          || f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)).ToArray(),
            files.Where(f => f.Contains("Migrations/", StringComparison.OrdinalIgnoreCase)).ToArray(),
            SecretScanner.Scan(patch), check.ExitCode == 0, productionLines > 0, testFiles.Count > 0);
    }

    private async Task<ProcessResult> Git(string directory, IReadOnlyList<string> args) =>
        await processes.RunAsync(new("git", args, directory), TimeSpan.FromMinutes(3));

    private static bool IsTest(string f) => f.Contains("test", StringComparison.OrdinalIgnoreCase) || f.Contains("spec", StringComparison.OrdinalIgnoreCase);
    private static bool IsProduction(string f) => ProductionExtensions.Contains(Path.GetExtension(f));
    private static bool IsGenerated(string f) => f.Contains("/obj/", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);
    private static bool IsSymlink(string file) =>
        (File.Exists(file) || Directory.Exists(file)) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint);
    private static bool IsBinary(string f)
    {
        if (!File.Exists(f)) return false;
        try
        {
            using var stream = File.OpenRead(f); var buffer = new byte[Math.Min(8000, (int)stream.Length)]; var read = stream.Read(buffer);
            return buffer.AsSpan(0, read).Contains((byte)0);
        }
        catch { return false; }
    }
    private static int CountAddedLines(string patch, HashSet<string> selected)
    {
        string? current = null; var count = 0;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal)) current = line[6..];
            else if (current is not null && selected.Contains(current) && line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)) count++;
        }
        return count;
    }
}

internal static class SecretScanner
{
    private static readonly Regex[] Patterns =
    [
        new(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase),
        new(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
        new(@"\bgh[pousr]_[A-Za-z0-9]{30,}\b"),
        new(@"\bsk-[A-Za-z0-9_-]{20,}\b"),
        new("""(?i)(?:password|pwd|secret|token|api[_-]?key)\s*[:=]\s*["']?[^\s"']{8,}""")
    ];

    public static IReadOnlyList<string> Scan(string patch)
    {
        var findings = new List<string>(); string? file = null; var lineNumber = 0;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal)) { file = line[6..]; lineNumber = 0; continue; }
            if (line.StartsWith("@@", StringComparison.Ordinal)) { lineNumber = ParseNewLine(line); continue; }
            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                if (Patterns.Any(p => p.IsMatch(line[1..]))) findings.Add($"{file}:{lineNumber}: possible secret-like value");
                lineNumber++;
            }
            else if (!line.StartsWith('-')) lineNumber++;
        }
        return findings;
    }

    private static int ParseNewLine(string line)
    {
        var match = Regex.Match(line, @"\+(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }
}
