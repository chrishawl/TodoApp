# TodoApp.Eval

A baseline-relative evaluation harness for the public `todo-search-v1`
search-and-pagination task. Codex implementation, deterministic evaluation,
and one evidence-gathering semantic grader run in isolated Docker containers.
The host controller prepares immutable inputs, owns all weights and arithmetic,
orchestrates containers, and retains results.

## Build and test

```sh
dotnet restore TodoApp.Eval.sln
dotnet test TodoApp.Eval.sln
./scripts/build-containers.sh
```

The images pin .NET SDK `9.0.305` and Codex CLI `0.147.0`, including its `codex-code-mode-host` and bundled `rg` companions. Override the Codex release intentionally with `CODEX_VERSION=x.y.z ./scripts/build-containers.sh`. Every run records both image IDs in `manifest.json`.

Normal unit tests use recorded provider fixtures and a local fake Git repository. They do not invoke Docker or consume model tokens.

## Public task

This public release contains `todo-search-v1`. Its pinned application baseline
is commit `549a3ece7a6c2f5bcf351bf6c5b08132f534ebc9`, and its task metadata is
recorded in `fixtures/todo-search-v1.json`. The task adds authenticated search
and pagination without a database migration or a new NuGet dependency. The
task text and architecture brief are kept in the evaluator source so prior
evaluation artifacts remain reproducible.

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
  --repo .. \
  --base 549a3ece7a6c2f5bcf351bf6c5b08132f534ebc9 \
  --implementation-cli codex \
  --implementation-model gpt-5.6-terra \
  --implementation-reasoning-effort high \
  --output ./results \
  --worktrees ./worktrees
```

Container isolation is the default for Codex. The implementation model and reasoning effort (`none`, `low`, `medium`, `high`, `xhigh`, or `max`) are required experiment parameters. Semantic grading defaults to the generic `agentic-v2` review profile and makes exactly one Codex `gpt-5.6-terra` call with `high` reasoning and a five-minute budget. `--review-profile agentic-v2` can pin the profile explicitly. `agentic-v1` remains readable for historical artifacts but cannot be selected for new runs. `--grader-model gpt-5.6-sol --grader-reasoning-effort high` is retained only for the calibration comparison; other grader configurations are rejected. The implementation timeout defaults to 30 minutes. Use `--cleanup` to remove the completed candidate; otherwise the complete candidate repository remains under `worktrees/<run-id>` for inspection or extraction.

`--isolation host` exists for local fake fixtures and compatibility checks. It is not an experiment-grade isolation boundary.

Use `baseline` with the same required options to capture a standalone baseline, and `report --result PATH` to print an existing report. `compare-results --results PATH` summarizes compatible retained runs by implementation model and effort without invoking another judge. It rejects mixtures of task, resolved-base, or profile hashes and labels single-run comparisons as descriptive. Exit codes are 0 for pass, 1 for candidate failure, and 2 for harness/configuration failure.

## Isolation model

Each run exports only an allowlist of application files from the selected Git commit into a fresh repository with one synthetic base commit. Evaluator source, private tests, and agent configuration are excluded even when they exist in the pinned commit. The candidate cannot inspect other commits, branches, worktrees, remotes, or the source repository’s `.git` data.

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

Before model tokens are spent, the harness restores and evaluates the detached baseline snapshot. A non-incremental analyzer-enabled build with zero diagnostics, a dependency audit with zero vulnerable-package findings, all 16 expected existing tests, `dotnet format --verify-no-changes`, `git diff --check`, and worktree cleanliness must pass absolutely. An unready baseline writes `baseline-readiness.json` and `harness-error.json` and exits 2.

NuGet auditing is disabled only for the format process so advisories cannot masquerade as formatting failures. The dedicated vulnerability command remains enabled. Analyzer diagnostics and package advisories must be absent from the baseline; candidate gates reject any newly introduced finding.

## Signals and artifacts

Every completed result directory retains the evaluation signals needed to audit both candidate quality and grader health:

- implementation and single semantic-grader token/tool/command/turn telemetry, aggregate metrics, and raw JSONL;
- frozen binary patch and candidate diff inventory;
- analyzer, format, vulnerability, existing-test, private-test, and coverage outputs;
- acceptance groups and hard gates;
- the profile-hashed 19-criterion semantic grade, dimension subtotals, semantic gates, typed evidence, findings, and coverage receipt;
- a concise decision report plus the full semantic grade, deterministic evidence, telemetry, baseline readiness, grader budget/health, image IDs, resource limits, and isolation settings in adjacent JSON artifacts.

The candidate repository remains the extraction artifact when `--cleanup` is omitted. Generated results never contain `auth.json`.

Container runs deliberately exclude host skills, configuration, and plugins. Current Codex JSONL does not expose structured skill-invocation data, so reports record skill usage as not exposed and observed skill names as unknown rather than zero.

## Semantic rubric and calibration

`review-profiles/agentic-v2/rubric.json` is the single source for six dimensions, 19 criterion IDs and weights, all five observable anchors, required-behavior criteria, and evidence requirements. The evaluator compiles both the grader prompt contract and strict output schema from that file. The model supplies levels and evidence but never returns weights or totals. The scoring module validates evidence, applies high/medium/low caps of 0/2/3, calculates semantic quality out of 100, and evaluates `requiredBehaviorComplete` and `noCriticalSemanticFinding`. Semantic quality is reported independently and is not converted into a composite score. Passing requires both semantic gates as well as every deterministic gate.

`review-profiles/agentic-v1` and its artifact reader are retained for auditability of historical runs.

`calibration/agentic-v2/cases.json` records human-agreed criterion and dimension ranges, expected findings, nearest-contrast ordering, and semantics-preserving perturbations. Retain three runs beneath `<results>/<case>/run-{1,2,3}/`. Promotion requires complete high-severity detection with no false highs, 90% of levels inside expected ranges, 95% within one adjacent level, same-patch standard deviation no greater than three points, perturbation movement no greater than three points with no pass/fail flip, and 90% preservation of expected absolute ordering. Verify a model run with:

```sh
dotnet run --project src/TodoApp.Eval -- calibration-check --results ./calibration-results/terra-high
```

Normal unit tests validate the rubric, scorer, evidence rules, one-call orchestration, calibration assertions, and reporting without invoking a model or spending tokens.
