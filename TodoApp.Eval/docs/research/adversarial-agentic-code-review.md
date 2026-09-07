# Adversarial agentic code review for `TodoApp.Eval`

Research date: 2026-08-25

## Executive recommendation

The likely project meant by “NWave” is [nWave-ai/nWave](https://github.com/nWave-ai/nWave). Its most transferable ideas are an implementation-independent peer reviewer, explicit test-integrity checks, proof that features are reachable through real entry points, and mutation testing. Bryan Finster's [Agentic Dev Team](https://github.com/bdfinst/agentic-dev-team) adds a useful orchestrator pattern: cheap deterministic gates first, narrowly focused semantic reviewers in parallel, structured findings, and bounded re-review. OpenAI's own [Codex review prompt](https://github.com/openai/codex/blob/main/codex-rs/core/review_prompt.md) supplies unusually good precision and severity rules.

For this eval, do not copy any one system whole. Replace the current single semantic reviewer with a fixed, versioned review profile:

1. Freeze the candidate and deterministic evidence.
2. Run cheap mechanical checks.
3. Run independent, mutually blinded bug hunters with non-overlapping lenses.
4. Give every material finding to a separate skeptic whose first job is to falsify it and whose verdict must cite executable or traceable evidence.
5. Give findings, challenges, and dissent to a read-only arbiter. Never decide by simple majority.
6. Let only adjudicated findings affect the candidate score. Preserve rejected and uncertain findings to measure judge precision.

Keep every judge role fixed at the same model and effort across implementation-model experiments. A cross-vendor lane can later be an experiment in judge quality, but changing judges while comparing Luna, Terra, and Sol would confound the result.

The current search-and-pagination task should remain unchanged. Parameterize
the harness around versioned task definitions and keep review profiles,
deterministic gates, and task assets independently versioned. Any future task
must be specified and validated separately rather than changing the contract or
interpretation of an existing task.

## What the current harness does

The current [`ReviewAndScoring.cs`](../../src/TodoApp.Eval/ReviewAndScoring.cs) runs one structured reviewer over the public task, architecture brief, frozen patch, and deterministic summary. It asks that reviewer to both assign all five semantic subscores and produce findings. The overall narrative grader then sees that review and independently confirms only critical-security status. The reviewer's self-assigned rubric score contributes 30 of 100 points.

Useful properties already in place:

- The patch is frozen before review.
- The reviewer is read-only.
- Deterministic checks and private tests are authoritative for hard gates.
- Judge model and effort are invariant across implementation experiments.
- A second role must confirm a critical-security hard gate.

The main weaknesses are correlated judgment and hindsight leakage. One model invocation both discovers issues and translates them into points; there is no independent bug hunt, no attempt to disprove findings, and no adjudication of non-security findings. The hunter also sees the private deterministic summary, so failed acceptance groups tell it where to look. That makes grading easier but does not measure whether the reviewer could discover the defect from the task, code, and diff.

### Local isolation issue to fix before parameterization

I independently verified that [`CreateWorkspaceAsync`](../../src/TodoApp.Eval/EvaluationRuntime.cs) currently runs `git archive <commit>` without an allowlisted pathspec and extracts the entire pinned tree into the candidate workspace. The current `todo-search-v1` pin is not leaking the harness because commit `23ce718...` predates `TodoApp.Eval/`; its tree contains neither the evaluator nor agent-control directories. Once the embedded harness and a task catalog are committed and a newer base is pinned, however, this behavior would expose evaluator prompts, task definitions, future tasks, and any committed private-test material to the implementation agent.

Before adding more task packages, make workspace export a positive, task-owned allowlist of application paths and root build files. Assert that evaluator, private-test, `.agents`, and `.codex` paths are absent, and record the exported file inventory and hashes in the manifest. Keep private task assets outside the candidate snapshot even though the harness itself lives in this repository. This is a stronger boundary than relying on agents to ignore files they can read.

## First-party and primary examples

### OpenAI Codex and Codex Security

OpenAI describes Codex code review as reasoning about the stated PR intent, the whole codebase and dependencies, and executing code and tests to validate behavior, rather than acting as a diff-only text classifier ([OpenAI, “Introducing upgrades to Codex”](https://openai.com/index/introducing-upgrades-to-codex/)).

The open-source [Codex review prompt](https://github.com/openai/codex/blob/main/codex-rs/core/review_prompt.md) is valuable because it optimizes for high-signal findings:

- A finding must be discrete, actionable, introduced by the patch, and something the author would actually fix.
- It cannot depend on an unstated assumption or merely speculate about breakage; the affected path must be identified.
- It must state the concrete input, environment, or scenario that triggers the problem.
- Severity must be calibrated to the trigger. `P0` is reserved for universal breakage, not a serious but conditional bug.
- Each finding includes a confidence score and a minimal line range; zero findings is acceptable.

Those rules are a strong base for the Todo eval's finding contract. “Adversarial” should mean relentless attempts to find and prove defects, not inflated severity or a long list of unverifiable concerns.

[Codex Security](https://help.openai.com/en/articles/20001107-codex-security) offers the stronger security pattern: build an editable threat model containing entry points, trust boundaries, sensitive data, and high-impact paths; identify a possible exploit path; attempt to reproduce it in an isolated validator; propose a minimal remediation; and revalidate after remediation. For this eval, use the first three stages and omit remediation because the candidate must remain frozen. A security finding should not become a hard gate until its attack path or authorization failure has been reproduced or otherwise independently evidenced.

### Mandiant's Agentic Vulnerability Discovery Harness

Mandiant's [Agentic Vulnerability Discovery Harness](https://cloud.google.com/blog/topics/threat-intelligence/staying-ahead-of-adversarial-ai-through-agentic-source-code-review) is the clearest first-party security analogue for the proposed pipeline. It enriches a codebase with architecture, asset, SBOM, and threat-intelligence context; generates a threat model; identifies and enriches entry points; and then has specialized access-control and data-flow agents generate vulnerability hypotheses. Fresh validation agents independently assess each hypothesis, a synthesis agent resolves their evidence, and human experts attempt dynamic reproduction before a finding is accepted.

Its benchmark design is equally relevant: proprietary synthetic codebases reduce training-contamination risk, injected defects are manually checked for reachability and exploitability, a dedicated grader matches reports to exact ground truth, and false positives and duplicates receive separate resolution. The Todo review benchmark should copy the threat-model → hypothesis → independent validation shape and use private, task-specific defects rather than assuming that a famous public bug corpus remains unseen.

### nWave

[nWave](https://github.com/nWave-ai/nWave) organizes work into specialized waves and human approval gates. The terminal delivery pair is acceptance design followed by Outside-In TDD implementation. Its documentation describes review and mutation testing as explicit delivery stages rather than optional afterthoughts ([nWave tutorial index](https://docs.nwave.ai/latest/guides/tutorials/)).

The most relevant source is its read-only [`nw-software-crafter-reviewer`](https://github.com/nWave-ai/nWave/blob/main/nWave/agents/nw-software-crafter-reviewer.md). It tells the reviewer to assume nothing and verify everything, then checks:

- whether tests were weakened, deleted, skipped, or changed between red and green;
- testing theater such as tautologies, no-assertion tests, or a fully mocked subject;
- whether tests enter through a driving port instead of exercising internals;
- whether the feature is externally invocable rather than merely present in an isolated class;
- whether acceptance behavior, TDD phase records, and quantitative gates agree;
- a structured verdict with defect ID, severity, location, evidence dimensions, and explicit gate states.

nWave's [reviewer invocation guide](https://docs.nwave.ai/latest/guides/invoke-reviewer-agents/) separates the artifact author from the reviewer and uses structured review status. That independence is the part to copy. Some project-specific rules—especially a universal `2 × behaviors` test budget and mandatory architectural style—are opinionated and should not become generic Todo eval requirements without an explicit task-level rationale.

Mutation testing is useful here as falsification, not as a vanity score. Use targeted mutants to ask whether submitted tests reject plausible wrong implementations. Do not reward raw mutation percentage without considering the task and equivalent mutants.

### Bryan Finster's Agentic Dev Team

Bryan Finster's [Agentic Dev Team](https://github.com/bdfinst/agentic-dev-team) implements `/specs → /plan → /build → /pr`. Its build workflow reviews specification compliance before code quality, and its `/code-review` workflow runs semantic lenses in parallel.

The authoritative [`code-review` skill](https://github.com/bdfinst/agentic-dev-team/blob/main/plugins/dev-team/skills/code-review/SKILL.md) and its [reader-friendly process description](https://devteam.bryanfinster.com/plugins/dev-team/docs/code-review-process/) contain several useful patterns:

- The coordinator does not review code; it scopes work, dispatches specialists, and aggregates structured JSON.
- Lint, type checking, secrets, and SAST run before model calls.
- Reviewers receive only the context their lens needs.
- Static-analysis findings are handed to semantic reviewers so they do not waste tokens rediscovering them.
- A `REVIEW-CONTEXT.md` artifact can supply institutional/domain knowledge.
- A bounded fix/re-review loop stops when it converges or escalates; it does not spin forever.
- A pre-commit gate is bound to the staged content, so changing code invalidates the prior review.

The most important evidence is a failure, not a success claim. Finster's [known-defect benchmark issue](https://github.com/bdfinst/agentic-dev-team/issues/821) records that an early reviewer roster scored roughly `0/4` recall against sampled Defects4J/BugsJS defects because the panel had architecture, structure, naming, and test lenses but no functional-correctness reviewer. The planned benchmark checks real buggy revisions against developer fixes and produces a dedicated missed-defects report. This is exactly why Todo's adversarial panel must include an explicit correctness/contract hunter and why the panel itself needs a defect corpus.

Finster's project also reports that review-agent structural findings separated workflow variants in its experiments when coverage and mutation largely did not ([experiment recommendations](https://devteam.bryanfinster.com/docs/experiments/RECOMMENDATIONS/)). Treat that as first-party experimental evidence, not independent validation, but retain the practical lesson: coverage, mutation, and semantic review measure different things.

### Cloudflare's production review coordinator

Cloudflare's April 2026 account of [orchestrating AI code review at scale](https://blog.cloudflare.com/ai-code-review/) reports a production design with up to seven specialized reviewers—covering security, performance, code quality, documentation, release concerns, and internal policy—followed by a stronger coordinator that inspects source, deduplicates findings, corrects categories, and filters false positives. The system uses structured severities and explicit “do not flag” rules for theoretical risks with unlikely preconditions, unchanged problems, adequate defense-in-depth, and preference-only library suggestions. Cloudflare also sanitizes user-controlled merge-request text that could imitate its prompt delimiters.

That is strong support for specialist discovery plus a signal/noise coordinator. For a comparative eval, retain every raw specialist finding and dissent rather than allowing the coordinator to erase them, and use a fixed panel rather than Cloudflare's adaptive risk tiers so every candidate receives comparable judge compute. Treat Cloudflare's scale and finding-rate numbers as self-reported operational evidence, not a ground-truth accuracy benchmark.

### A purpose-built adversarial review plugin

The small open-source [`ng/adversarial-review`](https://github.com/ng/adversarial-review) plugin is the closest concrete blueprint for an adversarial stage. It runs free mechanical checks, then an Optimizer pass that finds issues and a Skeptic pass that challenges them. It can add model/provider diversity, gates fixes on confidence, records disagreements, and verifies any applied changes in a bounded loop.

Its [design rationale](https://github.com/ng/adversarial-review/blob/main/docs/design-rationale.md) has two especially good rules:

- Critical/major verdicts require command-output evidence; lower-stakes or inherently qualitative findings may use reasoned evidence but have capped confidence.
- The skeptic is not instructed to disagree for theater. It should confirm, refute, or leave a claim uncertain based on evidence.

The plugin is a young, lightly adopted implementation, so it is an architectural example rather than proof that its thresholds are optimal. In the eval, do not auto-fix at all; preserve the frozen candidate and use the skeptic/arbiter stages only for reliable grading.

### Structured disagreement—and its false-consensus failure mode

The August 2026 workshop paper [“Adversarial Review: Structured Disagreement for Grounded Agentic Code Review”](https://arxiv.org/abs/2608.18167) tests a reviewer/critic exchange before the main editing agent acts. On its SWE-PRBench subset, a naive disagreement prompt performed worst among the reported configurations (`F1 0.457`): the agents often reached false consensus or added speculative concerns. Requiring the critic to state explicit, evidence-grounded disagreement improved the reported result to the best configuration in that subset (`F1 0.533`).

This is recent workshop evidence, not a settled general result, but the failure mode matters. The Todo skeptic should not be told to “reach agreement.” It must attach evidence, may preserve `uncertain` or dissenting verdicts, and should never convert repetition into confidence. Minimal structured disagreement with evidence is more useful than adding more conversational agents.

### Qodo PR-Agent and GitHub Copilot review

Qodo's open-source [PR-Agent reviewer prompt](https://github.com/the-pr-agent/pr-agent/blob/main/pr_agent/settings/pr_reviewer_prompts.toml) independently converges on several Codex rules: review only issues introduced by the patch; require a concrete trigger; be thorough on bugs/security but conservative on low-severity claims; emit structured output; and accept an empty findings list. These are useful anti-noise controls. Its default maximum of three findings is appropriate for PR readability but is a poor choice for an eval bug hunt because it can cap recall.

GitHub documents that Copilot review can use repository-wide, path-specific, and `AGENTS.md` guidance and that code review reads custom instructions from the pull request's **base branch** ([GitHub repository-instructions docs](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/add-custom-instructions/add-repository-instructions)). That is the right source-control trust boundary for a product review. The Todo evaluator should be stricter still: judge policy, task brief, and threat model come from evaluator-owned immutable inputs, while candidate code, comments, names, documentation, generated output, commit/PR text, and instruction files are untrusted evidence rather than authority.

### SEVRA-BENCH: social engineering the reviewer

[SEVRA-BENCH](https://arxiv.org/abs/2606.13757) targets this exact boundary. Its initial benchmark constructs 1,062 malicious pull requests from inverted real vulnerability fixes across the leading 2025 CWE categories, then wraps them in 15 social-engineering frames involving claimed evidence, urgency, prior approval, and authority. The attack is not only a hidden code defect: adversary-controlled PR language tries to shape how the reviewer interprets it. The accompanying [dataset](https://huggingface.co/datasets/RedAI4Code/SEVRA) also supplies benign controls so resistance is not measured by indiscriminately rejecting changes.

Add framing variants to the Todo judge benchmark: hold a diff constant while changing its task/PR narrative to invoke urgency, authority, prior approval, or fake validation. Measure both defect detection and false-decline rate on benign controls. Reviewers must ground conclusions in code, executable behavior, and evaluator-owned requirements, never in the candidate's confidence claims.

### Anthropic's multi-agent findings

Anthropic's vulnerability-research experiment used 45 agents with peer review and a separate arbiter to decide whether a submitted vulnerability was new and valid ([“Patterns and problems in emerging multiagent systems”](https://www.anthropic.com/research/multiagent-systems)). Its coordinated and independent approaches were largely complementary—only 12 vulnerabilities overlapped in the reported experiment—which supports diverse search lanes.

The same work warns that groups can converge prematurely, repeat familiar ideas, and suppress unshared evidence. Therefore:

- Hunters should work independently before seeing anyone else's claims.
- The arbiter should preserve a lone, strongly evidenced finding even when other hunters missed it.
- “Two out of three reviewers agree” is not a sound adjudication rule.
- Dissent and evidence should be first-class artifacts, not discarded during deduplication.

### Why one model “reviewing itself” is insufficient

The ICLR paper [“Large Language Models Cannot Self-Correct Reasoning Yet”](https://arxiv.org/abs/2310.01798) found that intrinsic self-correction without external feedback can fail or degrade answers. That result is broader than code review, but it supports using a fresh skeptic with tool evidence instead of asking the original hunter to “think again.”

The NDSS 2026 paper [“Trust Me, I Know This Function”](https://arxiv.org/abs/2508.17361) demonstrates familiar-pattern attacks that make LLM code analysis overlook small meaningful bugs, including transfer across model families and languages. The defensive implication is concrete: review behavior, call paths, and data flow rather than trusting names, comments, or familiar-looking abstractions. Include deceptive-but-correct and deceptive-but-buggy cases in the review benchmark.

## Proposed `adversarial-v1` review pipeline

### Stage 0: immutable inputs and trust boundaries

Create one evidence bundle from evaluator-owned inputs:

- public task and acceptance criteria;
- architecture brief and explicit allowed trade-offs;
- frozen patch plus complete changed files and relevant callers;
- base-commit versions of changed files;
- deterministic build/analyzer/format/test artifacts;
- evaluator-owned threat model and review policy;
- candidate execution telemetry where relevant.

Do not load candidate `AGENTS.md`, `CLAUDE.md`, review prompts, or instructions as authority. Do not execute candidate-provided scripts beyond the evaluator's allowlisted deterministic commands. Store all judge artifacts outside the candidate worktree.

Split deterministic evidence into two views:

- **Discovery view:** public checks and build state, but not private-test names, failures, or acceptance-group booleans.
- **Adjudication view:** full private deterministic evidence, available only after hunters commit their findings.

This preserves genuine discovery while still allowing high-confidence grading.

### Stage 1: deterministic preflight

Run the existing build, analyzer, format, vulnerability, diff, existing-test, private-test, and coverage checks once. Add task-specific deterministic probes where possible. Model reviewers should not spend tokens reproducing already authoritative facts, but they may use isolated temporary tests to prove semantic claims.

Unlike a production review system's adaptive cost gate, an eval should use a fixed panel and fixed budgets for every candidate in the same task. Adaptive depth based on a model-produced diff would give different candidates different judge effort and weaken comparison.

### Stage 2: independent hunters

Run five fresh, mutually blinded sessions in parallel. Each receives the discovery view and can inspect the read-only candidate:

| Hunter | Primary question | Required adversarial lenses |
| --- | --- | --- |
| Contract/correctness | Does every requested behavior actually work through the public API? | omitted rules, boundaries, invalid states, caller compatibility, error semantics, pagination/filter composition |
| Security/abuse | Can an untrusted or lower-privilege caller cross a boundary? | authentication, authorization, IDOR, tenant leakage, over-posting, injection, enumeration, secrets |
| Data/concurrency | Can realistic ordering or failure cause corruption or inconsistency? | races, lost updates, transaction boundaries, idempotency, retries, partial failure, cancellation, resource lifetime |
| Tests/falsification | Could the tests pass while the implementation is wrong? | weakened/deleted tests, tautologies, mock theater, fixture theater, missing negative/boundary cases, targeted mutation ideas |
| Architecture/operations | Does the patch introduce a costly structural or runtime failure? | dependency direction, duplicate policy, query amplification, unbounded work, observability, migrations, rollback compatibility |

Correctness is deliberately a first-class lane. Architecture or test-quality agents must not be expected to “also notice” functional bugs.

Each hunter returns only structured candidate findings. It must not assign the overall candidate score. A finding must include a trigger, affected observable outcome, exact source location, evidence already collected, a proposed falsification command/probe, severity, and confidence.

### Stage 3: skeptical validation

Normalize and deduplicate claims without dropping minority findings. Then send each claim to a fresh skeptic lane. The skeptic's job order is:

1. Try to prove the claim false.
2. If it survives, reproduce it with the narrowest safe command, test, trace, or concrete call path.
3. Check whether it was introduced by the candidate rather than inherited from the baseline.
4. Recalibrate severity against the actual trigger and impact.
5. Return `confirmed`, `rejected`, or `uncertain`—never rewrite the claim into a different finding.

For critical/high correctness, security, or data-integrity claims, command output or a complete executable trace is mandatory. For architecture and maintainability claims that cannot be executed, require caller/dependency evidence and cap confidence below the critical-hard-gate threshold.

Temporary probes should live in evaluator scratch space and must not alter the frozen patch. Store the command, exit code, relevant stdout/stderr excerpt, and artifact hash. “The code looks wrong” is not evidence.

### Stage 4: independent adjudication

A final read-only arbiter receives:

- normalized hunter claims;
- skeptic verdicts and evidence;
- disagreements and minority findings;
- the adjudication view of private deterministic evidence;
- the versioned task rubric and severity policy.

The arbiter may map evidence to rubric dimensions and decide whether a claim affects grading, but it may not invent new findings. If the private evidence reveals an issue no hunter found, record a **review miss** and let the deterministic task score handle the failure. Do not let the arbiter manufacture hindsight findings and make the panel look better than it was.

Critical security becomes a hard gate only when independently confirmed. One well-proved minority finding is sufficient; majority agreement is unnecessary. Conversely, multiple reviewers repeating the same speculation does not make it true.

### Stage 5: scoring and artifacts

Separate three outputs that are currently combined:

1. **Candidate semantic score:** points from adjudicated findings and explicit rubric decisions.
2. **Review-system quality:** recall against deterministic/seeded defects, unsupported-finding rate, evidence validity, severity calibration, and minority-find rescue.
3. **Narrative report:** a concise explanation derived from authoritative arithmetic, not a third opportunity to change scores.

Retain all of the following:

- raw outputs and telemetry for every hunter, skeptic, and arbiter;
- normalized claims and deduplication links;
- commands and evidence artifacts;
- confirmed, rejected, uncertain, and missed findings;
- per-lens token cost and wall time;
- the exact task and review-profile versions.

## Suggested finding contract

```json
{
  "id": "correctness-003",
  "lens": "contract-correctness",
  "title": "Cursor skips items sharing the same timestamp",
  "claim": "The next page predicate uses CreatedAt alone even though ordering also uses Id.",
  "trigger": "Two visible todos have the same CreatedAt and the page boundary falls between them.",
  "impact": "A valid todo is never returned on any page.",
  "introducedByPatch": true,
  "location": { "file": "Todo.Api/...", "line": 123 },
  "evidence": [
    {
      "kind": "command",
      "command": "dotnet test ... --filter ...",
      "exitCode": 1,
      "artifact": "review-evidence/correctness-003.txt"
    }
  ],
  "falsificationAttempt": "Repeated with reversed IDs and unique timestamps; only equal-key boundary fails.",
  "severity": "high",
  "confidence": 0.97,
  "validation": "confirmed",
  "adjudication": "score"
}
```

Required invariants:

- `trigger` and `impact` are mandatory for every correctness/security finding.
- A source line without behavioral evidence is not enough.
- `critical` is reserved for universal or realistically catastrophic failures under documented deployment assumptions.
- Pre-existing issues are reported separately and never reduce the candidate score.
- Suggestions and taste-based refactors cannot masquerade as defects.
- No maximum finding count; relevance and evidence control noise.

## Parameterizing tasks without losing the current task

A versioned task package should own all task-specific values rather than growing more static constants. Conceptually:

```text
tasks/
  task-v1/
    task.json
    public-task.md
    architecture-brief.md
    review-policy.md
    threat-model.md
    private-tests/
  another-task-v1/
    ...
review-profiles/
  structured-single-v1.json
  adversarial-v1.json
```

Important `task.json` fields:

- stable task ID and schema version;
- base commit and expected baseline test count;
- public-task and architecture-brief paths;
- private-test project/image inputs;
- acceptance-group IDs, weights, and hard gates;
- allowed/forbidden change surfaces;
- task-owned threat model, known risks, and severity overrides;
- review profile ID;
- resource budgets and expected artifacts.

The existing task should remain byte-for-byte stable as task packaging evolves.
A run manifest must record content hashes for every task and review input so
results remain comparable.

## Benchmark the reviewers before trusting them

Build a small judge benchmark alongside each task. Finster's known-defect experience shows why this is essential.

Create frozen candidate variants with one known defect each, plus clean controls:

- missing role check on one endpoint;
- ownership-transfer race;
- reusable or wrong-user invitation token;
- stale update silently overwrites a concurrent edit;
- audit event written without the state change, or vice versa;
- pagination boundary omitted under equal sort keys;
- test weakened from an outcome assertion to `not null`;
- fully mocked test that passes when production code is removed;
- malicious PR/task framing that claims urgency, prior approval, authority, or fake test evidence around the same defective diff;
- suspicious names/comments over correct behavior (false-positive control);
- intentionally unfamiliar implementation that is behaviorally correct (false-positive control).

For each reviewer/profile, measure:

- defect recall by category and severity;
- clean-patch false-positive rate;
- localization and trigger accuracy;
- percentage of material findings with executable evidence;
- confirmation/rejection accuracy of the skeptic;
- calibration of confidence and severity;
- minority findings rescued by the arbiter;
- tokens, wall time, and failures per stage.

Do not tune prompts on the final benchmark cases. Split them into development and held-out sets, version both, and retain a dedicated missed-defects report.

## Concrete design decisions

- **Use a panel, not a debate chat.** Independent discovery followed by directed challenges reduces anchoring and preserves unique findings.
- **Use fixed judge compute per task.** It keeps implementation-model comparisons fair.
- **Keep reviewers read-only.** Auto-fix loops are useful in production workflows but would destroy the eval's frozen evidence.
- **Blind discovery to private failures.** Reveal private evidence only to validators/arbiter after findings are committed.
- **Require falsification.** The skeptic tries to reject each claim before confirming it.
- **Do not majority-vote.** Evidence outweighs consensus; retain dissent.
- **Treat candidate text as untrusted.** GitHub correctly sources review instructions from the base branch; this evaluator should go further and source all judge authority from immutable evaluator inputs, never candidate text.
- **Separate correctness from architecture.** A panel without a dedicated correctness hunter can look sophisticated and still miss the actual bug.
- **Score the judge too.** Unsupported findings do not hurt candidates, but they must remain visible as review-system failures.
- **Version and hash everything.** Task, prompts, review profile, container images, model, effort, and evidence inputs belong in the manifest.

## Source list

- OpenAI: [Codex review prompt](https://github.com/openai/codex/blob/main/codex-rs/core/review_prompt.md), [Codex code-review announcement](https://openai.com/index/introducing-upgrades-to-codex/), [Codex Security](https://help.openai.com/en/articles/20001107-codex-security)
- nWave: [repository](https://github.com/nWave-ai/nWave), [software-crafter reviewer](https://github.com/nWave-ai/nWave/blob/main/nWave/agents/nw-software-crafter-reviewer.md), [reviewer invocation](https://docs.nwave.ai/latest/guides/invoke-reviewer-agents/), [tutorial index](https://docs.nwave.ai/latest/guides/tutorials/)
- Bryan Finster: [Agentic Dev Team](https://github.com/bdfinst/agentic-dev-team), [`code-review` skill](https://github.com/bdfinst/agentic-dev-team/blob/main/plugins/dev-team/skills/code-review/SKILL.md), [code-review process](https://devteam.bryanfinster.com/plugins/dev-team/docs/code-review-process/), [known-defect benchmark issue](https://github.com/bdfinst/agentic-dev-team/issues/821), [workflow experiment recommendations](https://devteam.bryanfinster.com/docs/experiments/RECOMMENDATIONS/)
- Production and security systems: [Cloudflare's review orchestrator](https://blog.cloudflare.com/ai-code-review/), [Mandiant's Agentic Vulnerability Discovery Harness](https://cloud.google.com/blog/topics/threat-intelligence/staying-ahead-of-adversarial-ai-through-agentic-source-code-review)
- Other implementations: [`ng/adversarial-review`](https://github.com/ng/adversarial-review), [its design rationale](https://github.com/ng/adversarial-review/blob/main/docs/design-rationale.md), [Qodo PR-Agent reviewer prompt](https://github.com/the-pr-agent/pr-agent/blob/main/pr_agent/settings/pr_reviewer_prompts.toml), [GitHub Copilot review instructions](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/add-custom-instructions/add-repository-instructions)
- Research: [Adversarial Review](https://arxiv.org/abs/2608.18167), [SEVRA-BENCH](https://arxiv.org/abs/2606.13757), [SEVRA dataset](https://huggingface.co/datasets/RedAI4Code/SEVRA), [Anthropic multi-agent patterns and problems](https://www.anthropic.com/research/multiagent-systems), [Huang et al. on intrinsic self-correction](https://arxiv.org/abs/2310.01798), [Bernstein et al. on familiar-pattern attacks](https://arxiv.org/abs/2508.17361)
