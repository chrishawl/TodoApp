using System.Diagnostics;
using System.Text;

namespace TodoApp.Eval;

internal sealed class ProcessRunner
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "The instance is the process-execution seam used throughout the harness and its tests.")]
    public async Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        TimeSpan timeout,
        string? stdoutPath = null,
        string? stderrPath = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = spec.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in spec.Arguments) startInfo.ArgumentList.Add(argument);
        if (spec.Environment is not null)
            foreach (var (key, value) in spec.Environment) startInfo.Environment[key] = value;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            if (!process.Start()) throw new HarnessException($"Failed to start {spec.FileName}.");
        }
        catch (Exception ex) when (ex is not HarnessException)
        {
            throw new HarnessException($"Could not start '{spec.FileName}'. Is the CLI installed and on PATH?", ex);
        }

        if (spec.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(spec.StandardInput);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
            {
                await Task.WhenAll(stdoutTask, stderrTask);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var finishedAt = DateTimeOffset.UtcNow;
        if (stdoutPath is not null) Write(stdoutPath, stdout);
        if (stderrPath is not null) Write(stderrPath, stderr);
        return new(process.ExitCode, timedOut, startedAt, finishedAt,
            Encoding.UTF8.GetByteCount(stdout), Encoding.UTF8.GetByteCount(stderr), stdout, stderr);
    }

    public async Task<string?> TryGetVersionAsync(string executable, string workingDirectory)
    {
        try
        {
            var result = await RunAsync(new(executable, ["--version"], workingDirectory), TimeSpan.FromSeconds(15));
            return result.ExitCode == 0 ? (result.Stdout + result.Stderr).Trim() : null;
        }
        catch (HarnessException) { return null; }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
