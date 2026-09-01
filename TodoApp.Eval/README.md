# TodoApp.Eval

A baseline-relative evaluation harness for the pinned TodoApp search-and-pagination task. Codex implementation, deterministic evaluation, and one evidence-gathering semantic grader run in isolated Docker containers. The host controller prepares immutable inputs, owns all weights and arithmetic, orchestrates containers, and retains results.

## Build and test

```sh
dotnet restore TodoApp.Eval.sln
dotnet test TodoApp.Eval.sln
./scripts/build-containers.sh
```

The images pin .NET SDK `9.0.305` and Codex CLI `0.147.0`, including its `codex-code-mode-host` and bundled `rg` companions. Override the Codex release intentionally with `CODEX_VERSION=x.y.z ./scripts/build-containers.sh`. Every run records both image IDs in `manifest.json`.

Normal unit tests use recorded provider fixtures and a local fake Git repository. They do not invoke Docker or consume model tokens.

## Authentication

Sign in once on the host:

```sh
codex login
codex login status
```

At the first run the harness copies `~/.codex/auth.json` into the dedicated `todoapp-eval-codex-auth-v1` Docker volume. The agent never receives the host `.codex` directory. The wrapper deletes everything in `CODEX_HOME` except `auth.json` before and after every invocation, and calls Codex with `--ignore-user-config`, `--ignore-rules`, and `--ephemeral`. Refreshed authentication is therefore retained without retaining configuration, rules, plugins, skills, history, or session rollouts.

To replace the cached identity after signing into a different account:

```sh
docker volume rm todoapp-eval-codex-auth-v1
```

The next run seeds it from the host again. `auth.json` contains access credentials and must never be committed or copied into result artifacts.

## Run

From this directory:

```sh
dotnet run --project src/TodoApp.Eval -- run \
  --repo /Users/chrishawley/dev/TodoApp \
  --base 23ce718962f125cccfa25c4adcb7e7a14360d79a \
  --implementation-cli codex \
  --implementation-model gpt-5.6-terra \
  --implementation-reasoning-effort high \
  --output ./results \
  --worktrees ./worktrees
```

Container isolation is the default for Codex. The implementation model and reasoning effort (`none`, `low`, `medium`, `high`, `xhigh`, or `max`) are required experiment parameters. Semantic grading makes exactly one Codex `gpt-5.6-terra` call with `high` reasoning and a five-minute budget. `--grader-model gpt-5.6-sol --grader-reasoning-effort high` is retained only for the one-time calibration comparison; other grader configurations are rejected. The implementation timeout defaults to 30 minutes. Use `--cleanup` to remove the completed candidate; otherwise the complete candidate repository remains under `worktrees/<run-id>` for inspection or extraction.

`--isolation host` exists for local fake fixtures and compatibility checks. It is not an experiment-grade isolation boundary.

Use `baseline` with the same required options to capture a standalone baseline, and `report --result PATH` to print an existing report. Exit codes are 0 for pass, 1 for candidate failure, and 2 for harness/configuration failure.

## Isolation model

Each run exports only the selected Git commit into a fresh repository with one synthetic base commit. The candidate cannot inspect other commits, branches, worktrees, remotes, or the source repository’s `.git` data.

The implementation container receives only:

- the candidate repository at `/workspace`;
- the authentication-only volume at `/codex-home`;
- a fresh per-run NuGet volume;
- a private tmpfs at `/tmp`;
- the public task over standard input.

It does not receive the host home directory, source repository, eval controller, results, private tests, Docker socket, SSH agent, environment secrets, or host configuration. The root filesystem is read-only; the process runs as UID 10001 with all Linux capabilities dropped, `no-new-privileges`, PID/CPU/memory limits, and no extra host mounts. Network access remains enabled because Codex and NuGet require it. Codex runs without its inner sandbox because the official CLI reserves that option for isolated runners; Docker is the security boundary.

The evaluator image is separate. Only after implementation completes does it receive the candidate plus the private tests baked into the image. Patch capture and diff inventory run inside this evaluator container, preventing candidate-created symlinks from inducing host-side file reads. Semantic grading runs in one fresh agent container with the candidate mounted read-only. Its discovery prompt contains compact build/test/diff evidence but no private acceptance-group names or outcomes.

The agent necessarily has access to its own Codex credential while running. The design protects the host filesystem and experiment inputs; it is not a credential broker or an outbound-domain firewall.

## Baseline readiness

Before model tokens are spent, the harness restores and evaluates the detached baseline snapshot. Analyzer-enabled build, all 16 expected existing tests, `dotnet format --verify-no-changes`, `git diff --check`, and worktree cleanliness must pass absolutely. An unready baseline writes `baseline-readiness.json` and `harness-error.json` and exits 2.

NuGet auditing is disabled only for the format process so advisories cannot masquerade as formatting failures. The dedicated vulnerability command remains enabled, and package advisories and analyzer diagnostics remain baseline-relative.

## Signals and artifacts

Every completed result directory retains the evaluation signals needed to audit both candidate quality and grader health:

- implementation and single semantic-grader token/tool/command/turn telemetry, aggregate metrics, and raw JSONL;
- frozen binary patch and candidate diff inventory;
- analyzer, format, vulnerability, existing-test, private-test, and coverage outputs;
- acceptance groups and hard gates;
- the profile-hashed 15-criterion semantic grade, typed evidence, findings, and coverage receipt;
- the evaluator-calculated grade, report, baseline readiness, grader budget/health, image IDs, resource limits, and isolation settings.

The candidate repository remains the extraction artifact when `--cleanup` is omitted. Generated results never contain `auth.json`.

Container runs deliberately exclude host skills, configuration, and plugins. Current Codex JSONL does not expose structured skill-invocation data, so reports record skill usage as not exposed and observed skill names as unknown rather than zero.

## Semantic rubric and calibration

`review-profiles/agentic-v1` contains the versioned rubric, stable grader prompt, and strict output schema. The model never returns weights or totals. C# validates criterion completeness and evidence, applies high/medium finding caps, normalizes applicable points to 30, and renders every criterion in the final report.

`calibration/agentic-v1/cases.json` records nine known-answer cases using only `mustFind`, `mustNotFind`, and criterion ceiling/floor assertions. After retaining `semantic-grade.json` and `manifest.json` for each case beneath a results directory, verify a model run with:

```sh
dotnet run --project src/TodoApp.Eval -- calibration-check --results ./calibration-results/terra-high
```

Normal unit tests validate the rubric, scorer, evidence rules, one-call orchestration, calibration assertions, and reporting without invoking a model or spending tokens.
