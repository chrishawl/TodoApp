namespace TodoApp.Eval.Tests;

public sealed class ProcessAndGitTests
{
    [Fact]
    public async Task Timeout_kills_process_tree()
    {
        var result = await new ProcessRunner().RunAsync(new("/bin/sh", ["-c", "sleep 5 & wait"], "/tmp"), TimeSpan.FromMilliseconds(150));
        Assert.True(result.TimedOut); Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Cancellation_kills_process_tree_and_propagates()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            new("/bin/sh", ["-c", "sleep 5 & wait"], "/tmp"), TimeSpan.FromSeconds(10), cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Missing_cli_is_a_harness_error()
    {
        await Assert.ThrowsAsync<HarnessException>(() => new ProcessRunner().RunAsync(new("definitely-not-a-real-eval-cli", [], "/tmp"), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Worktree_is_pinned_patch_includes_untracked_and_cleanup_removes_it()
    {
        using var fixture = await GitFixture.Create(); var runner = new ProcessRunner(); var git = new GitWorkspace(runner);
        var commit = await git.ResolveCommitAsync(fixture.Repository, "HEAD"); var worktree = Path.Combine(fixture.Root, "worktree");
        await git.CreateAsync(fixture.Repository, worktree, commit, "eval/test");
        Assert.Equal(commit, (await runner.RunAsync(new("git", ["rev-parse", "HEAD"], worktree), TimeSpan.FromSeconds(5))).Stdout.Trim());
        await File.WriteAllTextAsync(Path.Combine(worktree, "new.txt"), "new content\n");
        var patch = await git.CapturePatchAsync(worktree, commit); Assert.Contains("new.txt", patch); Assert.Contains("new content", patch);
        await git.RemoveAsync(fixture.Repository, worktree, "eval/test"); Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public async Task Fake_agent_edits_fixture_without_external_model()
    {
        using var fixture = await GitFixture.Create(); var script = Path.Combine(fixture.Root, "fake.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nprintf 'implemented\\n' > feature.cs\n");
        var adapter = new FakeAdapter(); var invocation = adapter.Build(new(CliProvider.Fake, "fixture"), fixture.Repository, "task", fixture.Root, script);
        var result = await new ProcessRunner().RunAsync(invocation.Process, TimeSpan.FromSeconds(5));
        Assert.Equal(0, result.ExitCode); Assert.True(File.Exists(Path.Combine(fixture.Repository, "feature.cs")));
    }

    [Fact]
    public void Private_test_copy_creates_target_for_top_level_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "todo-eval-copy-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target");
        try
        {
            Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "test.cs"), "fixture");
            DeterministicEvaluator.CopyDirectory(source, target);
            Assert.Equal("fixture", File.ReadAllText(Path.Combine(target, "test.cs")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Candidate_snapshot_allowlist_excludes_evaluator_and_agent_configuration()
    {
        var paths = DockerEvaluationRuntime.CandidateSnapshotPaths;

        Assert.Contains("TodoApp.sln", paths);
        Assert.Contains("Todo.Api", paths);
        Assert.Contains("Todo.Api.Tests", paths);
        Assert.DoesNotContain(paths, path => path.StartsWith("TodoApp.Eval", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.StartsWith(".agents", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.StartsWith(".codex", StringComparison.Ordinal));
    }
}

internal sealed class GitFixture : IDisposable
{
    public string Root { get; }
    public string Repository { get; }
    private GitFixture(string root) { Root = root; Repository = Path.Combine(root, "repo"); }
    public static async Task<GitFixture> Create()
    {
        var f = new GitFixture(Path.Combine(Path.GetTempPath(), "todo-eval-tests-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(f.Repository);
        var runner = new ProcessRunner();
        await Run(runner, f.Repository, "init"); await Run(runner, f.Repository, "config", "user.email", "eval@example.test"); await Run(runner, f.Repository, "config", "user.name", "Eval Test");
        await File.WriteAllTextAsync(Path.Combine(f.Repository, "README.md"), "fixture\n"); await Run(runner, f.Repository, "add", "."); await Run(runner, f.Repository, "commit", "-m", "fixture"); return f;
    }
    private static async Task Run(ProcessRunner r, string cwd, params string[] args)
    {
        var result = await r.RunAsync(new("git", args, cwd), TimeSpan.FromSeconds(10)); Assert.Equal(0, result.ExitCode);
    }
    public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
}
