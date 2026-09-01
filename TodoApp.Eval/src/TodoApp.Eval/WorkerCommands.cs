namespace TodoApp.Eval;

internal static class WorkerCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) throw new HarnessException("Worker command is required.");
        if (args[0] == "sdk-version")
        {
            var sdk = await new ProcessRunner().RunAsync(new("dotnet", ["--version"], "/tmp"), TimeSpan.FromMinutes(1));
            Console.Write(sdk.Stdout);
            Console.Error.Write(sdk.Stderr);
            return sdk.ExitCode;
        }

        var values = Parse(args.Skip(1).ToArray());
        var workspace = Required(values, "workspace");
        var timeout = TimeSpan.FromMinutes(int.TryParse(values.GetValueOrDefault("timeout-minutes"), out var minutes) ? minutes : 30);
        var processes = new ProcessRunner();
        if (args[0] == "restore")
        {
            var log = Required(values, "log");
            var result = await processes.RunAsync(new("dotnet", ["restore", Path.Combine(workspace, "TodoApp.sln"), "--nologo"], workspace), timeout, log, log + ".stderr");
            return result.ExitCode == 0 && !result.TimedOut ? 0 : 1;
        }
        if (args[0] != "evaluate") throw new HarnessException($"Unknown worker command '{args[0]}'.");

        var artifacts = Required(values, "artifacts");
        var baseCommit = Required(values, "base");
        var baselinePath = values.GetValueOrDefault("baseline");
        var baseline = baselinePath is null ? null : Json.Read<DeterministicResult>(baselinePath);
        var resultValue = await new DeterministicEvaluator(processes, new GitWorkspace(processes), AppContext.BaseDirectory)
            .EvaluateAsync(workspace, baseCommit, artifacts, timeout, baseline, values.ContainsKey("private"));
        Json.Write(Path.Combine(artifacts, "deterministic.json"), resultValue);
        File.WriteAllText(Path.Combine(artifacts, "patch.diff"),
            await new GitWorkspace(processes).CapturePatchAsync(workspace, baseCommit));
        return 0;
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new HarnessException($"Unexpected worker argument '{args[i]}'.");
            var key = args[i][2..];
            if (key == "private") { values[key] = "true"; continue; }
            if (++i >= args.Length) throw new HarnessException($"Worker option --{key} requires a value.");
            values[key] = args[i];
        }
        return values;
    }

    private static string Required(IReadOnlyDictionary<string, string?> values, string key) =>
        values.GetValueOrDefault(key) ?? throw new HarnessException($"Worker option --{key} is required.");
}
