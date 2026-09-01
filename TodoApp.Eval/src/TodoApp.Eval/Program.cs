namespace TodoApp.Eval;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "worker") return await WorkerCommands.RunAsync(args.Skip(1).ToArray());
            var (command, values) = OptionParser.Parse(args);
            return command switch
            {
                "run" => await Run(OptionParser.Eval(values)),
                "baseline" => await Baseline(OptionParser.Eval(values)),
                "report" => Report(values),
                "calibration-check" => CalibrationCheck(values),
                "help" => Help(),
                _ => throw new HarnessException($"Unknown command '{command}'.")
            };
        }
        catch (HarnessException ex)
        {
            Console.Error.WriteLine("Harness error: " + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static async Task<int> Run(EvalOptions options)
    {
        Validate(options);
        var started = DateTimeOffset.UtcNow;
        var runId = options.RunId ?? $"{started:yyyyMMdd-HHmmss}-{options.Implementation.Provider.ToString().ToLowerInvariant()}";
        ValidateRunId(runId);
        var reviewProfile = ReviewProfile.Load(AppContext.BaseDirectory, "agentic-v1");
        var graderBudget = GraderBudget.AgenticV1;
        var resultRoot = Path.Combine(options.OutputRoot, runId);
        if (Directory.Exists(resultRoot)) throw new HarnessException($"Result directory already exists and will not be overwritten: {resultRoot}");
        Directory.CreateDirectory(resultRoot);
        var artifactRoot = Path.Combine(resultRoot, "artifacts"); Directory.CreateDirectory(artifactRoot);
        var processes = new ProcessRunner(); var git = new GitWorkspace(processes);
        var resolved = await git.ResolveCommitAsync(options.Repository, options.BaseCommit);
        await using var runtime = await CreateRuntime(options, processes, runId);
        var worktreePath = Path.Combine(options.WorktreeRoot, runId);
        var baselinePath = Path.Combine(options.WorktreeRoot, runId + "-baseline");
        var sdk = await runtime.GetDotnetSdkAsync(options.Repository);
        var manifest = new RunManifest(runId, options.Repository, options.BaseCommit, resolved, worktreePath, resultRoot, sdk,
            options.Implementation, options.SemanticGrader, reviewProfile.Rubric.Id, reviewProfile.Hash, graderBudget,
            started, null, options.Cleanup,
            Isolation: options.Isolation, Container: runtime.Container);
        Json.Write(Path.Combine(resultRoot, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(resultRoot, "task.md"), TaskDefinition.PublicTask);

        EvalWorkspace? baselineWorkspace = null; EvalWorkspace? candidateWorkspace = null;
        try
        {
            baselineWorkspace = await runtime.CreateWorkspaceAsync(options.Repository, baselinePath, resolved, branch: null);
            var baselineRestore = await runtime.RestoreAsync(baselineWorkspace, Path.Combine(artifactRoot, "baseline-restore.log"), options.Timeout);
            if (!baselineRestore)
            {
                var restoreReadiness = BaselineReadinessPolicy.Assess(resolved, sdk, false, null);
                RecordBaselineReadiness(resultRoot, ref manifest, restoreReadiness);
                BaselineReadinessPolicy.RequireReady(restoreReadiness);
            }
            var baselineArtifacts = Path.Combine(resultRoot, "baseline");
            var baseline = await runtime.EvaluateAsync(baselineWorkspace, baselineArtifacts, options.Timeout, null, null, includePrivateTests: false);
            Json.Write(Path.Combine(resultRoot, "baseline.json"), baseline);
            var readiness = BaselineReadinessPolicy.Assess(resolved, sdk, true, baseline);
            RecordBaselineReadiness(resultRoot, ref manifest, readiness);
            BaselineReadinessPolicy.RequireReady(readiness);
            await runtime.RemoveWorkspaceAsync(options.Repository, baselineWorkspace); baselineWorkspace = null;

            var branch = "eval/" + runId;
            candidateWorkspace = await runtime.CreateWorkspaceAsync(options.Repository, worktreePath, resolved, branch);
            var restored = await runtime.RestoreAsync(candidateWorkspace, Path.Combine(artifactRoot, "candidate-restore.log"), options.Timeout);
            if (!restored) throw new HarnessException("Candidate dependency restore failed before implementation. See candidate-restore.log.");

            var implementationArtifacts = Path.Combine(artifactRoot, "implementation-agent");
            var execution = await runtime.Agents.ExecuteAsync(options.Implementation, candidateWorkspace.Path, TaskDefinition.PublicTask,
                implementationArtifacts, options.Timeout, readOnly: false, options.FakeScript);
            var telemetry = execution.Telemetry;
            var telemetryPath = Path.Combine(resultRoot, "telemetry.json");
            var runTelemetry = RunTelemetry.Create(telemetry);
            Json.Write(telemetryPath, runTelemetry);

            var deterministic = await runtime.EvaluateAsync(candidateWorkspace, artifactRoot, options.Timeout, baseline,
                Path.Combine(resultRoot, "baseline.json"), includePrivateTests: true);
            Json.Write(Path.Combine(resultRoot, "deterministic.json"), deterministic);
            var patch = File.ReadAllText(Path.Combine(artifactRoot, "patch.diff"));
            File.WriteAllText(Path.Combine(resultRoot, "patch.diff"), patch);
            var submittedDiscovered = deterministic.HardGates.GetValueOrDefault("submittedTests");
            runTelemetry = runTelemetry with
            {
                FilesChanged = deterministic.Diff.ChangedFiles.Count,
                TotalChurn = deterministic.Diff.AddedLines + deterministic.Diff.DeletedLines,
                ProductionToTestChurnRatio = deterministic.Diff.TestLines == 0
                    ? null
                    : (double)deterministic.Diff.ProductionLines / deterministic.Diff.TestLines,
                HasSubmittedTests = deterministic.Diff.HasSubmittedTests,
                SubmittedTestsDiscovered = submittedDiscovered
            };
            Json.Write(telemetryPath, runTelemetry);

            var implementationSucceeded = execution.Process.ExitCode == 0 && !execution.Process.TimedOut;
            var gradingCase = GradingCase.Create(candidateWorkspace.Path, candidateWorkspace.BaseCommit, patch,
                deterministic, implementationSucceeded, reviewProfile);
            var grader = new SemanticGrading(runtime.Agents, options.SemanticGrader, reviewProfile, artifactRoot,
                graderBudget, observed =>
                {
                    runTelemetry = runTelemetry.WithSemanticGrader(observed);
                    Json.Write(telemetryPath, runTelemetry);
                });
            var semantic = await grader.GradeAsync(gradingCase, CancellationToken.None);
            Json.Write(Path.Combine(resultRoot, "semantic-grade.json"), semantic);
            var grade = Scoring.Calculate(deterministic, semantic, implementationSucceeded);
            Json.Write(Path.Combine(resultRoot, "grade.json"), grade);
            var graderTelemetry = runTelemetry.SemanticGrader
                ?? throw new HarnessException("Semantic grader completed without publishing telemetry.");
            manifest = manifest with
            {
                FinishedAt = DateTimeOffset.UtcNow,
                SemanticGraderTelemetry = graderTelemetry,
                GraderHealth = GraderHealth.Assess(graderTelemetry, graderBudget),
                GraderCostInputs = new(1, graderTelemetry.Provider, graderTelemetry.RequestedModel,
                    graderTelemetry.ConfiguredReasoningEffort, graderTelemetry.Tokens)
            };
            Json.Write(Path.Combine(resultRoot, "manifest.json"), manifest);
            ReportWriter.Write(Path.Combine(resultRoot, "report.md"), manifest, runTelemetry, deterministic, semantic, grade);
            Console.WriteLine(Path.Combine(resultRoot, "report.md"));
            if (options.Cleanup) { await runtime.RemoveWorkspaceAsync(options.Repository, candidateWorkspace); candidateWorkspace = null; }
            return grade.Status == "PASS" ? 0 : 1;
        }
        finally
        {
            if (baselineWorkspace is not null) { try { await runtime.RemoveWorkspaceAsync(options.Repository, baselineWorkspace); } catch { } }
            if (candidateWorkspace is not null && options.Cleanup) { try { await runtime.RemoveWorkspaceAsync(options.Repository, candidateWorkspace); } catch { } }
        }
    }

    private static async Task<int> Baseline(EvalOptions options)
    {
        Validate(options);
        var runId = options.RunId ?? $"baseline-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"; ValidateRunId(runId);
        var root = Path.Combine(options.OutputRoot, runId);
        if (Directory.Exists(root)) throw new HarnessException($"Output already exists: {root}");
        Directory.CreateDirectory(root);
        var processes = new ProcessRunner(); var git = new GitWorkspace(processes);
        var resolved = await git.ResolveCommitAsync(options.Repository, options.BaseCommit);
        await using var runtime = await CreateRuntime(options, processes, runId);
        var sdk = await runtime.GetDotnetSdkAsync(options.Repository);
        EvalWorkspace? workspace = await runtime.CreateWorkspaceAsync(options.Repository, Path.Combine(options.WorktreeRoot, runId), resolved, null);
        try
        {
            var restored = await runtime.RestoreAsync(workspace, Path.Combine(root, "restore.log"), options.Timeout);
            if (!restored)
            {
                var restoreReadiness = BaselineReadinessPolicy.Assess(resolved, sdk, false, null);
                Json.Write(Path.Combine(root, "baseline-readiness.json"), restoreReadiness);
                BaselineReadinessPolicy.RequireReady(restoreReadiness);
            }
            var result = await runtime.EvaluateAsync(workspace, root, options.Timeout, null, null, false);
            Json.Write(Path.Combine(root, "baseline.json"), result);
            var readiness = BaselineReadinessPolicy.Assess(resolved, sdk, true, result);
            Json.Write(Path.Combine(root, "baseline-readiness.json"), readiness);
            BaselineReadinessPolicy.RequireReady(readiness);
            Console.WriteLine(Path.Combine(root, "baseline-readiness.json"));
            return 0;
        }
        finally { if (workspace is not null) await runtime.RemoveWorkspaceAsync(options.Repository, workspace); }
    }

    private static Task<IEvaluationRuntime> CreateRuntime(EvalOptions options, ProcessRunner processes, string runId) =>
        options.Isolation == IsolationKind.Container
            ? CreateDockerRuntime(options, processes, runId)
            : Task.FromResult<IEvaluationRuntime>(new HostEvaluationRuntime(processes));

    private static async Task<IEvaluationRuntime> CreateDockerRuntime(EvalOptions options, ProcessRunner processes, string runId) =>
        await DockerEvaluationRuntime.CreateAsync(processes, options, runId);

    private static void RecordBaselineReadiness(string resultRoot, ref RunManifest manifest, BaselineReadiness readiness)
    {
        Json.Write(Path.Combine(resultRoot, "baseline-readiness.json"), readiness);
        manifest = manifest with { BaselineReadiness = readiness, FinishedAt = readiness.Ready ? null : DateTimeOffset.UtcNow };
        Json.Write(Path.Combine(resultRoot, "manifest.json"), manifest);
        if (!readiness.Ready) Json.Write(Path.Combine(resultRoot, "harness-error.json"), new { phase = "baseline-readiness", readiness.Failures });
    }

    private static int Report(Dictionary<string, string?> values)
    {
        var result = values.GetValueOrDefault("result") ?? throw new HarnessException("report requires --result <run-directory>.");
        var grade = Json.Read<GradeResult>(Path.Combine(result, "grade.json"));
        var report = Path.Combine(result, "report.md");
        if (!File.Exists(report)) throw new HarnessException($"Report is missing: {report}");
        Console.WriteLine(File.ReadAllText(report)); return grade.Status == "PASS" ? 0 : 1;
    }

    private static int CalibrationCheck(Dictionary<string, string?> values)
    {
        var results = values.GetValueOrDefault("results")
            ?? throw new HarnessException("calibration-check requires --results <directory>.");
        var packPath = values.GetValueOrDefault("pack")
            ?? Path.Combine(AppContext.BaseDirectory, "calibration", "agentic-v1", "cases.json");
        var pack = CalibrationPack.Load(Path.GetFullPath(packPath));
        var profile = ReviewProfile.Load(AppContext.BaseDirectory, pack.RubricProfileId);
        pack.ValidateAgainst(profile);
        var configuration = new CalibrationConfiguration(
            values.GetValueOrDefault("model") ?? pack.SelectedConfiguration.Model,
            values.GetValueOrDefault("reasoning-effort") ?? pack.SelectedConfiguration.ReasoningEffort);
        if (configuration != pack.SelectedConfiguration && configuration != pack.ComparisonConfiguration)
            throw new HarnessException("Calibration configuration must be the pack's selected or comparison model/effort pair.");
        var result = CalibrationVerifier.VerifyRun(pack, Path.GetFullPath(results), configuration);
        if (result.Passed)
        {
            Console.WriteLine($"Calibration passed: {result.CasesChecked} cases.");
            return 0;
        }
        foreach (var failure in result.Failures) Console.Error.WriteLine(failure);
        return 1;
    }

    private static void Validate(EvalOptions options)
    {
        if (!Directory.Exists(options.Repository) || !Directory.Exists(Path.Combine(options.Repository, ".git"))) throw new HarnessException("--repo must be a Git repository.");
        if (options.Timeout <= TimeSpan.Zero) throw new HarnessException("Timeout must be positive.");
        if (options.Implementation.Provider == CliProvider.Codex)
            ReasoningEfforts.Require(options.Implementation.ReasoningEffort, "Implementation");
        if (options.SemanticGrader is not { Provider: CliProvider.Codex } ||
            (options.SemanticGrader.Model, options.SemanticGrader.ReasoningEffort) is not ("gpt-5.6-terra", "high") and not ("gpt-5.6-sol", "high"))
            throw new HarnessException("Semantic grading must use Codex gpt-5.6-terra/high or gpt-5.6-sol/high.");
    }

    private static void ValidateRunId(string id)
    {
        if (id.Length is < 1 or > 80 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw new HarnessException("Run id may contain only letters, digits, '-' and '_'.");
    }

    private static int Help()
    {
        Console.WriteLine("""
            TodoApp.Eval

            run --repo PATH --base COMMIT --implementation-cli codex --implementation-model MODEL --implementation-reasoning-effort EFFORT [options]
            baseline --repo PATH --base COMMIT --implementation-cli codex --implementation-model MODEL --implementation-reasoning-effort EFFORT [options]
            report --result RUN_DIRECTORY
            calibration-check --results DIRECTORY [--pack FILE] [--model MODEL --reasoning-effort EFFORT]

            Options: --isolation container|host (default container), --agent-image, --evaluator-image, --codex-auth,
                     --output, --worktrees, --implementation-executable,
                     --timeout-minutes (default 30), --run-id, --cleanup,
                     --grader-model and --grader-reasoning-effort (comparison runs only).
            Codex reasoning effort: none|low|medium|high|xhigh|max. Semantic grading defaults to one gpt-5.6-terra/high call;
            gpt-5.6-sol/high is the only supported comparison configuration.
            Exit codes: 0 pass, 1 candidate failure, 2 harness/configuration failure.
            """); return 0;
    }
}
