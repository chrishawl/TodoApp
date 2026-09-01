namespace TodoApp.Eval.Tests;

public sealed class AdapterTests
{
    [Fact]
    public void Codex_command_is_ephemeral_sandboxed_and_jsonl()
    {
        var invocation = new CodexAdapter().Build(Codex("gpt-x", "xhigh"), "/tmp/repo", "task", "/tmp/artifacts");
        Assert.Contains("--ephemeral", invocation.Process.Arguments); Assert.Contains("--json", invocation.Process.Arguments);
        Assert.Equal("workspace-write", After(invocation.Process.Arguments, "--sandbox")); Assert.Equal("gpt-x", After(invocation.Process.Arguments, "--model"));
        Assert.Equal("model_reasoning_effort=\"xhigh\"", After(invocation.Process.Arguments, "-c"));
        Assert.Equal(1, invocation.Process.Arguments.Count(x => x.StartsWith("model_reasoning_effort=", StringComparison.Ordinal)));
        Assert.Equal("task", invocation.Process.StandardInput);
    }

    [Fact]
    public void Claude_command_streams_without_session_and_has_allowlist()
    {
        var invocation = new ClaudeAdapter().Build(new(CliProvider.Claude, "sonnet"), "/tmp/repo", "task", "/tmp/artifacts");
        Assert.Equal("stream-json", After(invocation.Process.Arguments, "--output-format")); Assert.Contains("--no-session-persistence", invocation.Process.Arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", invocation.Process.Arguments);
    }

    [Fact]
    public void Copilot_command_prevents_interaction_and_uses_logs()
    {
        var invocation = new CopilotAdapter().Build(new(CliProvider.Copilot, "gpt-5"), "/tmp/repo", "task", "/tmp/artifacts");
        Assert.Equal("task", After(invocation.Process.Arguments, "--prompt")); Assert.Contains("--log-dir", invocation.Process.Arguments);
        Assert.Contains("--no-ask-user", invocation.Process.Arguments); Assert.DoesNotContain("--silent", invocation.Process.Arguments);
    }

    [Fact]
    public void Codex_fixture_parses_events_and_tokens()
    {
        var stdout = Fixture("codex.jsonl"); var spec = Codex("requested");
        var invocation = new CodexAdapter().Build(spec, "/tmp/repo", "task", "/tmp/artifacts");
        var telemetry = new CodexAdapter().Parse(spec, invocation, Result(stdout), "1.0");
        Assert.Equal(111, telemetry.Tokens.Input); Assert.Equal(22, telemetry.Tokens.CachedInput); Assert.Equal(37, telemetry.Tokens.Output);
        Assert.Equal(7, telemetry.Tokens.Reasoning); Assert.Equal(148, telemetry.Tokens.InputOutput); Assert.Equal(2, telemetry.Turns);
        Assert.Equal("gpt-test", telemetry.ReportedModel); Assert.Equal(6, telemetry.ToolCalls); Assert.Equal(4, telemetry.ShellCommands);
        Assert.Equal(2, telemetry.FailedTools); Assert.Equal(1, telemetry.RepeatedCommands);
        Assert.Equal(1, telemetry.BuildInvocations); Assert.Equal(2, telemetry.TestInvocations);
        Assert.Equal(4, telemetry.ObservedShellCommands!.Count);
    }

    [Fact]
    public void Claude_fixture_parses_exposed_metrics_only()
    {
        var adapter = new ClaudeAdapter(); var invocation = adapter.Build(new(CliProvider.Claude, "requested"), "/tmp/repo", "task", "/tmp/artifacts");
        var telemetry = adapter.Parse(new(CliProvider.Claude, "requested"), invocation, Result(Fixture("claude.jsonl")), "1.0");
        Assert.Equal(13, telemetry.Tokens.Input); Assert.Equal(7, telemetry.Tokens.Output); Assert.Null(telemetry.Tokens.Reasoning); Assert.Equal("Done", telemetry.FinalResponse);
    }

    [Fact]
    public void Copilot_fixture_does_not_estimate_tokens()
    {
        var adapter = new CopilotAdapter(); var invocation = adapter.Build(new(CliProvider.Copilot, "gpt-5"), "/tmp/repo", "task", "/tmp/artifacts");
        var telemetry = adapter.Parse(new(CliProvider.Copilot, "gpt-5"), invocation, Result(Fixture("copilot.txt")), "1.0");
        Assert.Null(telemetry.Tokens.Input); Assert.Null(telemetry.Tokens.Output); Assert.True(telemetry.ClaimedUnobservedTests);
    }

    [Fact]
    public void Fake_judge_can_use_role_specific_executable_without_implementation_script()
    {
        var invocation = new FakeAdapter().Build(new(CliProvider.Fake, "fixture", "/tmp/fake-judge"), "/tmp/repo", "judge", "/tmp/artifacts");

        Assert.Equal("/tmp/fake-judge", invocation.Process.FileName);
        Assert.Empty(invocation.Process.Arguments);
        Assert.Equal("judge", invocation.Process.Environment!["EVAL_PROMPT"]);
    }

    [Fact]
    public void Codex_defaults_to_container_isolation_and_host_auth_cache()
    {
        var (_, values) = OptionParser.Parse([
            "run", "--repo", "/tmp/repo", "--base", "HEAD",
            "--implementation-cli", "codex", "--implementation-model", "gpt-test",
            "--implementation-reasoning-effort", "medium"
        ]);

        var options = OptionParser.Eval(values);

        Assert.Equal(IsolationKind.Container, options.Isolation);
        Assert.EndsWith(Path.Combine(".codex", "auth.json"), options.CodexAuthFile);
        Assert.Equal("todoapp-eval-agent:local", options.AgentImage);
        Assert.Equal("todoapp-eval-evaluator:local", options.EvaluatorImage);
        Assert.Equal("medium", options.Implementation.ReasoningEffort);
        Assert.Equal(new(CliProvider.Codex, "gpt-5.6-terra", ReasoningEffort: "high"), options.SemanticGrader);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("extreme")]
    public void Codex_implementation_requires_supported_reasoning_effort(string? effort)
    {
        var args = new List<string>
        {
            "run", "--repo", "/tmp/repo", "--base", "HEAD",
            "--implementation-cli", "codex", "--implementation-model", "gpt-test"
        };
        if (effort is not null) { args.Add("--implementation-reasoning-effort"); args.Add(effort); }
        var (_, values) = OptionParser.Parse([.. args]);

        Assert.Throws<HarnessException>(() => OptionParser.Eval(values));
    }

    [Theory]
    [InlineData("review-model")]
    [InlineData("review-reasoning-effort")]
    [InlineData("grader-cli")]
    [InlineData("grader-executable")]
    public void Judge_overrides_are_rejected(string option)
    {
        var (_, values) = OptionParser.Parse([
            "run", "--repo", "/tmp/repo", "--base", "HEAD",
            "--implementation-cli", "codex", "--implementation-model", "gpt-test",
            "--implementation-reasoning-effort", "high", "--" + option, "override"
        ]);

        var error = Assert.Throws<HarnessException>(() => OptionParser.Eval(values));
        Assert.Contains("no longer supported", error.Message);
    }

    [Fact]
    public void Sol_high_is_allowed_as_the_temporary_grader_comparison()
    {
        var (_, values) = OptionParser.Parse([
            "run", "--repo", "/tmp/repo", "--base", "HEAD",
            "--implementation-cli", "codex", "--implementation-model", "gpt-test",
            "--implementation-reasoning-effort", "high",
            "--grader-model", "gpt-5.6-sol", "--grader-reasoning-effort", "high"
        ]);

        Assert.Equal(new(CliProvider.Codex, "gpt-5.6-sol", ReasoningEffort: "high"), OptionParser.Eval(values).SemanticGrader);
    }

    [Theory]
    [InlineData("gpt-5.6-sol", "medium")]
    [InlineData("gpt-5.6-terra", "medium")]
    [InlineData("gpt-5.6-luna", "medium")]
    public void Unsupported_grader_combinations_are_rejected(string model, string effort)
    {
        var (_, values) = OptionParser.Parse([
            "run", "--repo", "/tmp/repo", "--base", "HEAD",
            "--implementation-cli", "codex", "--implementation-model", "gpt-test",
            "--implementation-reasoning-effort", "high",
            "--grader-model", model, "--grader-reasoning-effort", effort
        ]);

        Assert.Throws<HarnessException>(() => OptionParser.Eval(values));
    }

    [Fact]
    public void Codex_missing_usage_and_model_remain_unknown()
    {
        const string stdout = """
            {"type":"turn.started"}
            {"type":"item.started","item":{"type":"command_execution","command":"dotnet test"}}
            {"type":"turn.completed"}
            malformed
            """;
        var spec = Codex("configured"); var adapter = new CodexAdapter();
        var telemetry = adapter.Parse(spec, adapter.Build(spec, "/tmp/repo", "task", "/tmp/artifacts"), Result(stdout), null);

        Assert.Null(telemetry.ReportedModel); Assert.Null(telemetry.CliVersion);
        Assert.Null(telemetry.Tokens.Input); Assert.Null(telemetry.Tokens.Output);
        Assert.Equal(1, telemetry.Turns); Assert.Equal(0, telemetry.ToolCalls);
    }

    [Fact]
    public void Isolated_codex_records_skill_observability_as_unavailable_and_unknown()
    {
        var spec = Codex("configured"); var adapter = new CodexAdapter();
        var invocation = adapter.Build(spec, "/tmp/repo", "task", "/tmp/artifacts");
        invocation = invocation with
        {
            Process = invocation.Process with { Arguments = ["--ignore-user-config", .. invocation.Process.Arguments] }
        };

        var telemetry = adapter.Parse(spec, invocation, Result(""), "1.0");

        Assert.False(telemetry.HostSkillsAvailable);
        Assert.False(telemetry.ProviderUsageExposed);
        Assert.Null(telemetry.ObservedSkillNames);
        Assert.Equal("high", telemetry.ConfiguredReasoningEffort);
        Assert.Equal("configured", telemetry.ConfiguredModel);
    }

    [Fact]
    public void Fake_adapter_defaults_to_host_isolation()
    {
        var (_, values) = OptionParser.Parse([
            "run", "--repo", "/tmp/repo", "--base", "HEAD",
            "--implementation-cli", "fake", "--implementation-model", "fixture"
        ]);

        Assert.Equal(IsolationKind.Host, OptionParser.Eval(values).Isolation);
    }

    [Fact]
    public void Agent_container_mounts_only_auth_and_per_run_nuget_volumes()
    {
        var arguments = DockerEvaluationRuntime.AgentVolumeMountArguments("todoapp-eval-nuget-run1");

        Assert.Contains("type=volume,src=todoapp-eval-codex-auth-v1,dst=/codex-home", arguments);
        Assert.Contains("type=volume,src=todoapp-eval-nuget-run1,dst=/nuget", arguments);
        Assert.Equal(2, arguments.Count(x => x == "--mount"));
    }

    [Fact]
    public void Container_run_keeps_stdin_and_hardens_the_outer_boundary()
    {
        var arguments = DockerEvaluationRuntime.CommonRunArguments("eval-test");

        Assert.Contains("--interactive", arguments);
        Assert.Contains("--read-only", arguments);
        Assert.Contains("no-new-privileges", arguments);
        Assert.Contains("ALL", arguments);
    }

    private static string After(IReadOnlyList<string> args, string name) => args[args.ToList().IndexOf(name) + 1];
    private static AgentSpec Codex(string model, string effort = "high") => new(CliProvider.Codex, model, ReasoningEffort: effort);
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static ProcessResult Result(string stdout) => new(0, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, stdout.Length, 0, stdout, "");
}
