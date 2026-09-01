# Agentic graders and a discriminating code-quality rubric for `TodoApp.Eval`

Research date: 2026-09-01

## Executive recommendation

The concern is justified. The current semantic score is compressed at the ceiling and is not yet a defensible measure of code quality. Across the five recorded completed reviews, the totals are `0, 9, 10, 9, 10`. Excluding the no-op candidate, every substantive candidate receives 9 or 10 out of 10. Security, architecture/efficiency/consistency, maintainability, and test quality are at their maximum in all four; only correctness ever moves. That is almost no discriminatory signal for 30% of the overall grade.

The immediate cause is not that the judge model is necessarily too weak. The contract in [`ReviewAndScoring.cs`](../../src/TodoApp.Eval/ReviewAndScoring.cs) gives it five labels and numeric bounds, but no behavioral anchors, per-dimension evidence rules, reference facts, examples, `unknown` state, or calibration standard. One invocation must both discover defects and award the points after seeing the entire deterministic result, including private acceptance outcomes. It then reports its own total; the core validates only range and arithmetic and multiplies that total by three. Although the judge is launched through an agent executor, the fixed `sol/high` recorded review is operationally a one-shot model grader: one turn, zero tool calls, zero shell commands, and 8.1 seconds of wall time.

The recommended target is a **layered hybrid**:

1. The deterministic core owns an immutable, versioned rubric; criterion weights; applicability; hard gates; score caps; aggregation; and audit records.
2. Mechanical evaluators continue to own facts that code can establish: compilation, tests, analyzers, formatting, dependency risk, coverage, diff integrity, and selected performance or mutation checks.
3. Read-only specialist agents become **evidence producers**, not score authorities. They inspect the repository, trace call paths, formulate findings, and, where allowed, run targeted verification. Separate discovery from verification and final scoring.
4. The core converts adjudicated evidence into points. No model supplies an authoritative total.
5. A pairwise/reference lane is used to improve leaderboard discrimination and diagnose close calls, not as the sole pass/fail mechanism.
6. A human-labelled calibration set continuously tests judge precision, recall, agreement, order sensitivity, repeated-run variance, and ceiling rate before a grader profile is promoted.

This complements the earlier [`adversarial-agentic-code-review.md`](./adversarial-agentic-code-review.md). That note designs the bug-hunting and skeptic/arbiter pipeline. This report supplies the missing measurement layer: what quality means, how evidence earns points, how alternative judging modes fit together, and how to prove that the grader itself works.

## 1. Diagnosis of the current evaluator

### 1.1 What the code currently measures

The current semantic result has five integer dimensions:

| Dimension | Range | Recorded substantive scores |
| --- | ---: | --- |
| Correctness and intent | 0–3 | 2, 3, 2, 3 |
| Security, authorization, input | 0–2 | 2, 2, 2, 2 |
| Architecture, efficiency, consistency | 0–2 | 2, 2, 2, 2 |
| Maintainability | 0–2 | 2, 2, 2, 2 |
| Test quality | 0–1 | 1, 1, 1, 1 |

The prompt says only that scores must be within those bounds. [`Validate`](../../src/TodoApp.Eval/ReviewAndScoring.cs) proves that the numbers add up; it cannot prove that any point was deserved. [`Scoring.Calculate`](../../src/TodoApp.Eval/ReviewAndScoring.cs) then makes the self-reported semantic total worth 30 of 100 points.

The observed runs are:

| Run | Semantic total | Findings | Salient outcome |
| --- | ---: | ---: | --- |
| `container-e2e-004` | 0 | 1 | No implementation; 14/16 private cases failed |
| `container-e2e-005` | 9 | 1 | Unicode/SQLite case-folding defect found |
| `container-e2e-007` | 10 | 0 | No defect found |
| `luna-max` | 9 | 1 | Required build failed |
| `terra-high` | 10 | 0 | No defect found |

This is a small sample, so it does not establish population-level judge accuracy. It does establish a local design problem: the rubric does not distinguish the implementations that the harness has actually produced.

`luna-max` exposes the missing consistency rule especially clearly: the reviewer emits a **high** finding that the required build does not pass, yet still awards 9/10, including the maximum in security, architecture/efficiency/consistency, maintainability, and test quality. The deterministic grade correctly fails the candidate, but the semantic component still contributes 27/30. A material finding currently has no defined cap or deduction and can coexist with an almost-perfect semantic score.

There is also a useful disagreement example. The `container-e2e-005` reviewer identifies that translating `.ToUpper()` into SQLite `upper()` is ASCII-only and drops correctness from 3 to 2. The `container-e2e-007` implementation uses the analogous `.ToLower()` pattern, but its reviewer returns 3 with no finding. SQLite's own documentation confirms that built-in `lower()` and `upper()` case conversion is ASCII-only ([SQLite built-in scalar functions](https://sqlite.org/lang_corefunc.html)). These are not identical patches or a controlled repeatability trial, but they are exactly the kind of semantically related cases a calibration corpus should contain.

### 1.2 Why the scores saturate

Several effects reinforce each other:

1. **The ordinal levels are undefined.** A number such as “maintainability 2/2” has no stated observation that separates it from 1/2. The easiest completion is to grant the maximum when no obvious defect is noticed.
2. **Absence of a finding is treated as positive evidence.** The prompt never requires affirmative evidence for each awarded point. A missed defect and genuinely excellent code therefore produce the same score.
3. **Discovery and scoring are coupled.** The same invocation that decides what to inspect also decides whether its inspection was sufficient. There is no independent attempt to falsify a finding or a no-finding verdict.
4. **Private-test results create a halo.** The reviewer sees successful acceptance groups before it judges semantic correctness, test quality, security, and design. Those signals are valuable to a final scorer but can anchor a bug hunter toward “already correct.”
5. **Composite labels hide trade-offs.** “Architecture, efficiency, consistency” can be maximized without saying whether all three were examined or whether strength in one compensated for weakness in another.
6. **The judge is not actually verifying.** A one-turn, zero-tool review of roughly 19,000 input tokens is structured prompting, not agentic code investigation. It cannot run a focused test, inspect provider behavior, trace an authorization path, or query call sites.
7. **No reference or counterexamples exist.** The judge is not shown a task-specific risk list, known-good characteristics, known-bad variants, or calibrated examples of each level.
8. **The task suite is itself near saturation.** One relatively bounded endpoint task with strong private tests cannot establish whether a semantic grader separates broader architecture, concurrency, reliability, or maintainability quality. A better grader cannot manufacture variance that the candidate/task distribution does not contain.
9. **There is no grader evaluation.** A model name and high reasoning setting are experiment controls, not evidence of validity. No labelled defect set, human agreement target, repeated-run test, position-swap test, or score-distribution test gates changes to the grader.

The broader literature predicts several of these risks. MT-Bench found position, verbosity, and self-enhancement biases in LLM judges and showed that few-shot examples improved consistency but did not guarantee accuracy ([Zheng et al., 2023](https://arxiv.org/abs/2306.05685)). G-Eval improved alignment by supplying explicit evaluation steps and a form-filling structure, but still reported concern about bias toward LLM-generated text ([Liu et al., 2023](https://aclanthology.org/2023.emnlp-main.153/)). In coding specifically, CodeJudgeBench found significant randomness, sensitivity to response order, and better accuracy for pairwise comparison than scalar pointwise scoring across its experiments ([Jiang et al., 2025](https://arxiv.org/abs/2507.10535)). None of those results proves the exact failure mode in TodoApp, but all argue against treating one uncalibrated scalar call as ground truth.

## 2. Judging approaches and where each fits

No single judging mode dominates on accuracy, cost, auditability, and absolute scoring. The useful design is a portfolio with explicit responsibilities.

| Approach | Best use | Main strengths | Main failure modes | TodoApp fit |
| --- | --- | --- | --- | --- |
| Deterministic checks | Observable behavior and policy | Reproducible, cheap, debuggable | Incomplete oracle; can reward test gaming | Authoritative foundation and hard gates |
| Pointwise rubric | Absolute, reportable score | Easy aggregation; criterion-level explanation | Leniency/ceiling; poorly anchored scales | Keep, but atomize and anchor it |
| Reference-based judge | Task facts and known risks | Gives the judge a stable target | Gold implementation bias; stale or faulty reference | Use reference **facts**, never require structural imitation |
| Pairwise judge | Rank close candidates | Easier discrimination; avoids inventing absolute intervals | Position bias; no standalone meaning; O(n²) comparisons | Secondary leaderboard/tie-break lane |
| Process/trace judge | Agent behavior and efficiency | Explains how outcome arose; catches unsafe shortcuts | Can punish valid alternative workflows; trace privacy and volume | Separate process score, not product-quality substitute |
| Specialist agent | Repository-scale semantic evidence | Can search, trace, run focused checks, and revise hypotheses | Cost, nondeterminism, prompt injection, false positives | Recommended for evidence discovery/validation |
| Jury/ensemble | Reduce single-model idiosyncrasy | Diversity and uncertainty signal | Correlated errors, false consensus, high cost | Escalate only risky or disputed criteria |
| Human expert | Calibration and adjudication | Best available construct grounding | Slow, costly, human disagreement | Gold-set creation and periodic audit |

### 2.1 Deterministic and execution-based evaluation

Compilation, targeted acceptance tests, regression tests, static analysis, package auditing, and exact structural policies should stay authoritative. SWE-bench's evaluator, for example, declares a task resolved only when both issue-specific fail-to-pass tests and regression-oriented pass-to-pass tests succeed ([SWE-bench paper](https://arxiv.org/abs/2310.06770), [official grading code](https://github.com/SWE-bench/SWE-bench/blob/main/swebench/harness/grading.py)). That is a useful model for separating a behavioral oracle from open-ended review.

Enhancements for TodoApp:

- Add task-owned property, boundary, or metamorphic tests for risks the public examples do not expose.
- Add targeted mutation testing of the changed production surface. Mutation testing deliberately alters code and checks whether tests fail; survivors are evidence of weak fault detection, not proof that production code is wrong ([Stryker documentation](https://stryker-mutator.io/docs/)). Do not demand 100% because equivalent mutants cannot always be detected automatically ([Stryker equivalent-mutant documentation](https://stryker-mutator.io/docs/mutation-testing-elements/equivalent-mutants/)).
- Add executable query-shape or budget checks when a task has performance requirements: bounded round trips, server-side filtering, ordering before pagination, and no accidental full materialization.
- Preserve baseline-relative gates, but record raw evidence and distinguish infrastructure failures from candidate failures.

Trade-off: deterministic tests are the strongest evidence for specified cases, but no finite suite proves all semantic properties. They should constrain semantic scoring, not eliminate it.

### 2.2 Anchored pointwise scoring

Pointwise scoring asks for an absolute level independently for each candidate. It remains appropriate when the output must say “this patch earned 23/30” and when every run must stand alone. Its weakness is asking a model to invent what a number means.

Prometheus was built around fine-grained score rubrics plus a reference answer and reported substantially stronger evaluation performance when those materials were present ([Kim et al., 2023](https://arxiv.org/abs/2310.08491)). OpenAI's HealthBench uses a more atomic variant: domain experts wrote criterion-specific positive or negative requirements with point values, a model decides whether each criterion is met, and code sums the met criteria ([OpenAI HealthBench](https://openai.com/index/healthbench/)). These do not establish that the same design will automatically work for code, but they support moving from a holistic “give 0–2” instruction to independently judged rubric items.

Enhancements for TodoApp, in ascending strength:

1. Define every level with observable anchors and at least one positive and negative example.
2. Require evidence and counterevidence for every dimension before a level is emitted.
3. Score atomic criterion cards independently; have code calculate dimension totals.
4. Apply deterministic caps from adjudicated defects so a material defect and a maximum score cannot coexist.
5. Generate the qualitative review first, hide point weights during discovery, and calculate the score only after findings are frozen.

Trade-off: this is the cheapest useful upgrade and the easiest to audit. It will still miss bugs that a one-shot model never discovers.

### 2.3 Reference-based grading

A reference can be one of three different things:

- an executable behavior oracle, such as private tests;
- a list of task-specific facts and risk cases;
- a gold implementation patch.

The first two are useful. The third should be used only to generate hypotheses, never as a structural template. Multiple designs can satisfy the same requirement, and similarity to a gold patch would reward imitation rather than quality. MT-Bench's reference-guided experiment reduced the reported failure rate on reasoning questions from 70% to 15%, while also documenting that a faulty reference can mislead the judge ([Zheng et al., 2023](https://arxiv.org/abs/2306.05685)). That is both the argument for references and the warning label.

Enhancements for TodoApp:

- Give the grader evaluator-owned **reference facts**: SQLite case-folding limitations, minimal-API binding behavior, authorization invariants, pagination overflow risks, and expected query semantics.
- Give each fact an applicability condition and a primary source or executable probe.
- Keep the gold patch hidden from normal scoring; use it offline to create contrastive tests and likely-risk cards.
- Include multiple known-good implementations in calibration so the judge learns behavioral equivalence rather than one preferred structure.

Trade-off: references improve recall on anticipated risks but can narrow the judge's search and become stale. Version them with the task.

### 2.4 Pairwise and tournament grading

Pairwise grading asks which of A and B is better on a stated dimension, with an explicit tie. It reduces the cognitive burden of mapping quality onto an absolute scale and, in CodeJudgeBench, outperformed scalar pointwise judging. The same study found that swapping candidate order can substantially affect accuracy ([Jiang et al., 2025](https://arxiv.org/abs/2507.10535)). OpenAI's GDPval uses blinded expert pairwise comparisons and detailed rubrics as its primary grading standard; its automated grader is explicitly described as less reliable than expert graders ([OpenAI GDPval](https://openai.com/index/gdpval/)).

Enhancements for TodoApp:

- Compare anonymized patches independently per dimension, not “overall vibes.”
- Run both `A/B` and `B/A`; accept a preference only if the result is invariant, otherwise record a tie or escalate.
- Use balanced comparison graphs or Swiss-style rounds rather than every possible pair as the candidate pool grows.
- Fit a Bradley–Terry-style latent ranking offline, but retain raw wins, losses, ties, and order flips in the report.
- Compare candidates with an intentionally “sound but ordinary” anchor patch to distinguish below-standard, standard, and exceptional work.

Trade-off: pairwise results are excellent for ranking experiments and detecting compressed pointwise scores, but they do not by themselves define an absolute pass threshold. They also complicate reproducibility when the candidate pool changes.

### 2.5 Process and trajectory grading

Agent-as-a-Judge extends a one-shot judge with workspace reading, retrieval, graph/search operations, and intermediate evidence. On the DevAI proof-of-concept—55 development tasks with 365 manually annotated requirements—it reported closer agreement with human consensus than the one-shot LLM-as-a-Judge baseline, especially on dependent requirements ([Zhuge et al., 2024](https://arxiv.org/abs/2410.10934)). OpenAI's Agents SDK tracing records model generations, tool calls, handoffs, guardrails, and custom spans, illustrating the kind of structured trace a process evaluator can consume ([OpenAI Agents SDK tracing](https://openai.github.io/openai-agents-python/tracing/)).

Useful TodoApp process criteria are narrow and observable:

- whether required tests were actually executed rather than merely claimed;
- whether the agent weakened, skipped, or replaced evaluator-relevant tests;
- whether it inspected the relevant entry point and authorization path;
- whether its final claims are supported by captured command output;
- whether it accessed forbidden inputs or attempted to manipulate the harness;
- tool failures, repeated commands, wall time, and token use as efficiency signals.

Do **not** fold general trajectory style into the product-quality score. A direct implementation can be excellent after a messy search, and a polished trace can end in defective code. Keep outcome quality and process quality as adjacent metrics. Use hard process gates only for integrity or safety violations.

Trade-off: trace grading is diagnostically rich and important when comparing coding agents, but can accidentally privilege a particular solver strategy. OpenAI Evals' solver documentation explicitly separates evaluation logic—what is graded—from solver strategy—how the actor attempts it ([OpenAI Evals solver design](https://github.com/openai/evals/blob/main/evals/solvers/README.md)).

### 2.6 Specialist agents and juries

A specialist grader is worthwhile when it can do something the structured prompt cannot: inspect surrounding source, trace a security boundary, locate every call site, run a targeted command, or test a concrete counterexample. Merely naming a one-shot prompt “security agent” adds little.

The strongest practical division is:

- **correctness/contract hunter**: maps requirements to paths and searches untested boundaries;
- **security/authorization hunter**: traces identity, tenancy, validation, data flow, and trust boundaries;
- **test-efficacy analyst**: checks whether submitted tests fail for plausible wrong implementations;
- **design/maintainability analyst**: judges change-local complexity, coupling, consistency, and unnecessary abstraction;
- **verifier/skeptic**: tries to falsify each material claim with source or executable evidence;
- **arbiter**: resolves evidence conflicts without changing the frozen patch.

OpenAI's published Codex review prompt provides a good finding standard: issues should be discrete, actionable, severity-calibrated, tied to a triggering scenario, and accompanied by confidence and a tight location; zero findings is valid ([OpenAI Codex review prompt](https://github.com/openai/codex/blob/main/codex-rs/core/review_prompt.md)). The earlier TodoApp adversarial-review note develops this into a full hunter → skeptic → arbiter protocol.

An ensemble can reduce model-specific bias when its members are genuinely diverse. The Panel of LLM evaluators paper reported that several smaller models from disjoint families outperformed a single large judge across its tested datasets while reducing intra-model bias and cost ([Verga et al., 2024](https://arxiv.org/abs/2404.18796)). However, a majority is not evidence. Anthropic's 2026 multi-agent experiments found complementary vulnerability search, but also premature convergence and suppression of unshared decisive evidence; its vulnerability workflow used peer review and a separate arbiter rather than raw voting ([Anthropic, “Patterns and problems in emerging multiagent systems”](https://www.anthropic.com/research/multiagent-systems)).

Enhancements for TodoApp:

- Blind hunters to each other's findings until their first pass is frozen.
- Use different lenses before different model vendors; role diversity is cheaper and easier to control.
- Preserve minority findings and the evidence attached to them.
- Escalate only material or disputed claims to a second model to bound cost.
- Never average raw semantic totals from correlated judges. Adjudicate claims, then calculate one score in code.

Trade-off: a specialist panel buys recall and falsification at materially higher cost and latency. Use it on high-value experiments or risk-triggered dimensions, not automatically as seven full-repository chats.

## 3. A defensible model of “good code”

“Clean code” should not be a freestanding taste score. Readability, naming, comments, small cohesive units, and simple control flow are evidence about analyzability and modifiability. They matter when they reduce the cost or risk of understanding and changing the system; they should not reward a specific aesthetic or a higher abstraction count.

The sources converge on a practical model:

- ISO/IEC 25010:2023 defines a product-quality model with nine characteristics and subcharacteristics intended for specifying, measuring, and evaluating software quality ([ISO/IEC 25010:2023](https://www.iso.org/standard/78176.html)).
- ISO/IEC 5055 measures violations of architectural and coding practices that can create operational risk or excessive cost, rather than equating syntax style with quality ([ISO/IEC 5055:2021](https://www.iso.org/standard/80623.html)).
- Google's code-review guide asks reviewers to examine design, functionality and edge cases, complexity and over-engineering, tests, naming, comments, style, consistency, documentation, the whole changed context, and codebase health. It explicitly treats undocumented personal style preferences as non-blocking ([Google Engineering Practices](https://google.github.io/eng-practices/review/reviewer/looking-for.html), [Google's review standard](https://google.github.io/eng-practices/review/reviewer/standard.html)).
- NIST's SSDF makes secure development an explicit lifecycle concern rather than assuming general development practices automatically cover it ([NIST SP 800-218](https://csrc.nist.gov/pubs/sp/800/218/final)).
- The Software Engineering Institute frames quality as a desired combination of attributes and recommends scenario-based evaluation because attributes such as performance, security, reliability, and modifiability have different analysis techniques and can trade off ([SEI quality-attribute principles](https://www.sei.cmu.edu/library/principles-for-evaluating-the-quality-attributes-of-a-software-architecture/), [SEI ATAM](https://www.sei.cmu.edu/library/atam-method-for-architecture-evaluation/)).

### 3.1 Recommended dimensions for coding-agent patches

The following is a patch-level operationalization, not a claim that these are all of ISO 25010 or that every task needs every subcriterion.

| Dimension | What “good” means | Strong evidence | Do not reward |
| --- | --- | --- | --- |
| Functional suitability and intent | The complete stated behavior works, including untested boundaries, and the implementation matches user intent | Requirement-to-path mapping, private/property tests, verified edge cases, reachable entry point | Merely passing visible tests; similarity to a gold patch |
| Security and data protection | Correct authentication, authorization, tenant isolation, input handling, secret/dependency hygiene, and safe failure modes | Traced trust boundary, negative authorization tests, validated attack path, NIST/OWASP-aligned rule | Generic “seems secure”; speculative vulnerabilities |
| Reliability and robustness | Predictable error behavior, boundary safety, data integrity, concurrency/cancellation behavior where applicable | Boundary/failure tests, overflow analysis, transactional reasoning, deterministic behavior | Defensive complexity unrelated to a realistic scenario |
| Performance efficiency | Work is bounded and appropriate for expected load; filtering and projection happen in the right layer | Query plan/SQL, allocation or latency evidence, bounded I/O and round trips | Micro-optimizations without a requirement or measurement |
| Architecture and codebase fit | The change uses existing seams and conventions, keeps responsibilities coherent, and introduces only justified dependencies | Call graph, dependency direction, local conventions, minimal reachable implementation | Abstraction for hypothetical reuse; enforcing an external architecture style |
| Maintainability and analyzability | A future maintainer can understand, diagnose, test, and modify the change with contained impact | Clear names, cohesive units, limited coupling, explicit invariants, useful “why” comments, low incidental complexity | Line-count worship, mandatory patterns, formatting already enforced mechanically |
| Test efficacy and maintainability | Tests express behavior, exercise real entry points at the right level, fail for plausible defects, and remain deterministic/readable | Mutation kills, negative and boundary cases, public-interface tests, useful assertions, repeatability | Coverage percentage alone, assertion count, excessive mocking |
| Compatibility, operability, and documentation | Existing consumers and workflows remain valid; changed public/operational behavior is documented when needed | Regression tests, API/schema checks, relevant documentation, deploy/observe evidence | Documentation for internal details that did not change |

Microsoft's .NET guidance supports treating tests as their own quality artifact: good unit tests are fast, isolated, repeatable, self-checking, and timely; it also warns that high coverage does not imply high code quality ([Microsoft .NET unit-test guidance](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)). Google's guide similarly asks whether tests are correct, sensible, useful, and would actually fail when code breaks ([Google Engineering Practices](https://google.github.io/eng-practices/review/reviewer/looking-for.html)).

### 3.2 Separate universal dimensions from task-specific scenarios

A generic rubric should define the vocabulary; each task should activate only relevant scenarios. For the Todo search task, relevant scenarios include:

- an administrator still sees only their own rows;
- missing database identity is forbidden even with a valid external identity;
- filtering precedes count and pagination;
- a page calculation cannot overflow into an invalid offset;
- non-ASCII case-insensitive matching behaves as the public contract promises;
- the query does not load all todos before filtering or paging;
- submitted tests can detect owner-filter removal, order removal, count-after-page, invalid binding, and case-folding defects.

For a concurrency task, optimistic-conflict and idempotency scenarios would replace most of those. This keeps “modern engineering quality” tied to actual system risk rather than a universal checklist that rewards ceremony.

## 4. Concrete rubric and scoring mechanisms

### 4.1 Criterion cards, not five unexplained numbers

Store a versioned evaluator-owned rubric profile outside the prompt string. Each criterion card should contain:

```json
{
  "id": "correctness.unicode-casefold",
  "dimension": "functionalSuitability",
  "weight": 2,
  "applicability": "Task promises case-insensitive title search on SQLite",
  "mode": "hybrid",
  "levels": {
    "0": "Contradicted or not implemented",
    "1": "Partially works but a material supported case fails",
    "2": "Requirement is met with affirmative code or executable evidence"
  },
  "requiredEvidence": ["file/line or command artifact", "triggering input"],
  "referenceFacts": ["SQLite default lower/upper folding is ASCII-only"],
  "hardGate": false
}
```

Recommended fields also include `notApplicablePolicy`, `allowedEvidenceKinds`, `counterexamples`, `relatedDeterministicSignals`, `severityCap`, and `rubricVersion`. Applicability is decided by the task profile or deterministic core, not improvised by the judge.

The judge returns one record per criterion:

```json
{
  "criterionId": "correctness.unicode-casefold",
  "level": 1,
  "confidence": 0.94,
  "evidence": [
    {"kind": "source", "file": "Todo.Api/Todos/TodoApi.cs", "line": 75},
    {"kind": "reference", "id": "sqlite.casefold.ascii-only"}
  ],
  "counterevidence": ["ASCII mixed-case integration test passes"],
  "findingIds": ["F-003"]
}
```

The core rejects missing criteria, invalid evidence references, out-of-range levels, duplicate IDs, judge-created `N/A`, and totals supplied by the model. It resolves evidence IDs against frozen artifacts and computes the score.

### 4.2 Bottom-up scoring anchors

For qualitative criteria that need more than yes/no, use a common four-level maturity shape:

| Level | Meaning | Evidence rule |
| ---: | --- | --- |
| 0 | Missing, contradicted, unreachable, or materially unsafe | Direct failure, absent path, or validated critical/high defect |
| 1 | Partial or fragile | Some required behavior/evidence exists, but a material scenario or design obligation is unresolved |
| 2 | Sound | All applicable baseline obligations are affirmatively evidenced; no unresolved material finding |
| 3 | Exemplary | Sound plus task-relevant positive evidence of unusual robustness, simplicity, test efficacy, or measured efficiency |

Start at zero and earn levels. Level 2 is the expected production standard. Level 3 is deliberately rare and cannot be awarded merely because the hunter found nothing. This creates headroom without penalizing a direct, ordinary solution for not being “clever.”

The existing 30 semantic points could be distributed as a starting profile:

| Dimension | Points | Reason |
| --- | ---: | --- |
| Unobserved functional suitability and intent | 8 | Highest user impact; complements rather than repeats private acceptance groups |
| Security, authorization, and data protection | 5 | High downside; current task has identity and tenant boundaries |
| Reliability and robustness | 3 | Boundary, overflow, failure, and data-integrity risks |
| Architecture and codebase fit | 4 | Integration, responsibility, and unnecessary-abstraction judgment |
| Performance efficiency | 3 | Query behavior is central to search/pagination |
| Maintainability and analyzability | 4 | Change-local clarity, coupling, and modifiability |
| Test efficacy and maintainability | 3 | Submitted tests as fault detectors, not mere presence |
| **Total** | **30** | |

This weighting is a proposal to validate, not a universal standard. Compatibility/documentation can be activated by replacing points from lower-relevance dimensions on tasks where it matters. Avoid scoring the same fact twice: if a private test already awards acceptance points, the semantic criterion should cover an unobserved risk, explain the implementation quality, or be removed.

### 4.3 Finding-to-score consistency

Two viable mechanisms are:

**Criterion attainment.** Findings identify which criterion anchors are not met; the core awards the highest level whose full conditions remain satisfied. This is recommended because every lost point has a reason.

**Defect deduction.** Begin with a documented baseline and subtract fixed, severity-weighted defects. This is easier to retrofit but risks double counting correlated findings and implies maximum quality before evidence is gathered.

If deductions are retained during migration, add deterministic consistency caps:

- validated critical defect in a dimension → dimension level 0; security may also hard-fail;
- validated high defect → at most level 1;
- validated medium defect → at most level 2;
- validated low defect → at most level 3;
- no finding → no automatic minimum and no automatic maximum.

Severity must describe impact and trigger conditions, not reviewer confidence. Confidence decides whether to auto-accept, verify, or escalate a claim. Do not multiply points by self-reported confidence; a judge could improve a score by expressing certainty rather than producing better evidence.

### 4.4 Positive and negative criteria

Use both:

- positive: “Every query is owner-scoped before any projection, count, or page operation.”
- negative: “No candidate-controlled instruction or test manipulation changes evaluator policy.”

Each should be atomic enough to decide independently. HealthBench's criterion-level approach is a relevant precedent, but code rubrics should rely more heavily on executable and location evidence than natural-language response grading ([OpenAI HealthBench](https://openai.com/index/healthbench/)).

### 4.5 Evidence tiers

Define admissible evidence in the core:

1. **Executable:** focused test, query capture, mutation outcome, analyzer result.
2. **Traceable source:** specific file/line plus reachable call or data-flow explanation.
3. **Authoritative reference:** platform documentation or task-owned invariant applied to cited code.
4. **Reasoned qualitative:** maintainability/design argument tied to a concrete change and consequence.
5. **Speculation:** no concrete trigger or source; never score-affecting.

Critical/high claims should normally require tier 1 or a very strong tier 2+3 combination. Qualitative maintainability findings can use tier 4 but should not trigger hard gates. The official Codex review contract's insistence on an actionable scenario, calibrated severity, confidence, and exact location is a good minimum finding format ([OpenAI Codex review prompt](https://github.com/openai/codex/blob/main/codex-rs/core/review_prompt.md)).

## 5. Specialist agent or structured prompt in the core?

The right answer is **both, with a strict ownership boundary**.

### Keep in deterministic code

- rubric/profile selection and version;
- criterion definitions, weights, applicability, and N/A policy;
- hard gates and severity caps;
- evidence-schema validation and artifact hashes;
- score arithmetic and report generation;
- model/configuration pinning and equal-compute policy;
- calibration-suite execution and promotion thresholds.

These are evaluation policy. Making an agent decide them at runtime makes the measurement unstable and hard to audit.

### Put in a specialist agent

- repository navigation and context retrieval;
- requirement-to-code mapping;
- untested edge-case discovery;
- authorization/data-flow tracing;
- design and maintainability analysis;
- targeted, read-only verification commands;
- evidence-backed finding generation;
- challenges to other agents' claims.

These require semantic search and adaptive investigation. Hard-coding every possible software defect into C# is neither feasible nor desirable.

### Keep prompts versioned but outside orchestration code

The current interpolated C# string conflates orchestration and evaluation policy. Move prompts, JSON schemas, reference facts, and rubric cards into a versioned `review-profiles/<version>/` artifact embedded in the evaluator image. The core hashes the profile into the manifest. This makes rubric changes reviewable, allows offline replay, and prevents an accidental wording edit from silently changing the benchmark.

Candidate code, comments, tests, commit messages, and repository instruction files must be treated as untrusted evidence, never as grader authority. The specialist remains read-only; only evaluator-owned system/developer instructions define policy.

### Four implementation options

| Option | Change | Cost | Expected benefit | Recommendation |
| --- | --- | ---: | --- | --- |
| A. Anchored one-shot v2 | Criterion cards, evidence fields, core arithmetic; still no tools | Low | Removes the worst ambiguity and score inconsistency | Do immediately |
| B. Single evidence agent | Fixed read-only agent can inspect/search/run focused checks; separate score pass | Medium | Better recall and repository grounding | Default target |
| C. Risk-triggered specialists | Add security/test/performance specialist only when task profile activates it; skeptic verifies material claims | Medium–high | Higher recall without full-panel cost on every task | Recommended after calibration |
| D. Full panel plus arbiter | Independent hunters, validators, arbiter, optional model diversity | High | Best coverage and uncertainty evidence | Research/high-stakes lane, not initial default |

## 6. Calibration and validation of the grader

A grader is another model-based system and needs its own benchmark. “Looks reasonable on a few outputs” is not sufficient.

### 6.1 Build a gold calibration corpus

Create versioned cases from:

- current candidate patches;
- expert-authored sound solutions with materially different designs;
- real defects found in prior runs;
- single-defect variants made by controlled mutation;
- weak-test variants whose production behavior is unchanged;
- over-engineered but correct variants;
- security, authorization, performance, overflow, localization, cancellation, and regression cases;
- benign controls for every adversarial case;
- prompt-injection text in comments, names, test output, and documentation;
- pairs that differ only in author/model label, verbosity, formatting, or presentation order.

For each case, two qualified humans independently record criterion levels and findings, then adjudicate disagreements. Preserve disagreement instead of pretending every qualitative item has one obvious truth. OpenAI's GDPval uses occupation experts, blind comparison, critiques, rankings, and detailed rubrics; it also keeps its automated grader subordinate to the expert standard ([OpenAI GDPval](https://openai.com/index/gdpval/)). Google's sufficient-context autorater work similarly created an expert-labelled set before measuring the model classifier against it ([Google Research](https://research.google/blog/deeper-insights-into-retrieval-augmented-generation-the-role-of-sufficient-context/)).

### 6.2 Measure more than correlation

Track at least:

- finding precision, recall, and F1, split by severity and defect family;
- exact and within-one-level agreement per rubric criterion;
- weighted agreement for ordinal levels and rank correlation for total scores;
- false-positive rate on benign controls;
- repeated-run score variance and finding-set overlap;
- pairwise order-flip rate after `A/B` ↔ `B/A` swaps;
- ceiling rate, floor rate, score entropy, and candidate-rank stability;
- confidence calibration, such as accuracy by confidence bin or Brier score;
- monotonicity: inserting a known defect must not improve the affected score, and fixing it should not reduce that score;
- invariance to model/vendor names, harmless renaming, comment verbosity, and candidate-provided claims;
- cost and latency per correctly validated material finding.

Class imbalance matters. Agent-as-a-Judge explicitly notes that aggregate alignment can mislead when “requirement met” is rare and uses precision-recall analysis as a clearer view ([Zhuge et al., 2024](https://arxiv.org/abs/2410.10934)). TodoApp should report both defect recall and benign-case precision rather than one agreement percentage.

### 6.3 Predeclare promotion gates

Starting targets for an internal `grader-v2` can be:

- at least 95% recall for critical/high seeded defects;
- no more than 5% false positives on benign controls;
- at least 85% exact agreement with the adjudicated human criterion level, with ≥95% within one level;
- no more than 5% pairwise order flips after bidirectional judging;
- no unexplained semantic-score change greater than one point across repeated identical runs;
- less than 20% maximum scores on the deliberately challenging calibration set;
- 100% schema, evidence-reference, arithmetic, and hard-gate consistency.

These are proposed engineering gates, not published universal thresholds. Measure the current grader first, then set final thresholds before inspecting the replacement's results. Do not tune until the new grader merely beats a moving target.

### 6.4 Run an ablation ladder

Evaluate the same frozen corpus with:

1. current prompt;
2. anchors only;
3. atomic criterion cards;
4. reference facts;
5. agent tools;
6. separate hunter and scorer;
7. skeptic validation;
8. pairwise overlay;
9. model-diverse panel.

Report marginal recall, precision, agreement, variance, cost, and wall time at every step. The Agent-as-a-Judge paper's component ablations are the right scientific pattern: test whether reading, locating, retrieval, or other modules contribute rather than assuming “more agentic” is better ([Zhuge et al., 2024](https://arxiv.org/abs/2410.10934)).

### 6.5 Continuously challenge saturation

A non-saturating grader also needs a non-saturating task distribution. Add a task catalog spanning:

- authorization and cross-tenant behavior;
- concurrency and optimistic conflicts;
- multi-file architecture changes;
- backwards-compatible API evolution;
- query performance and resource bounds;
- partial failure, retry, and idempotency;
- test-only or refactoring tasks where maintainability can vary without acceptance failures.

Maintain hidden “canary defects” that are never used to optimize implementation agents. Re-run the grader calibration suite whenever its model, reasoning effort, prompt, reference facts, tool access, or evaluator version changes.

## 7. Recommended layered architecture

```text
frozen task + candidate + profile hash
                 |
       deterministic evidence
                 |
      blinded semantic discovery
   correctness | security | tests/design
                 |
      focused verifier / skeptic
                 |
       adjudicated evidence ledger
                 |
 deterministic rubric engine + caps
                 |
 absolute score/report ---- optional pairwise ranking
                 |
 calibration metrics + human audit queue
```

### Stage 0: freeze and classify inputs

Hash the candidate patch, task profile, reference facts, rubric, prompts, model IDs, reasoning settings, and tool policy. Separate evaluator authority from untrusted candidate content.

### Stage 1: deterministic evidence

Run current gates plus task-relevant mutation, property, query-shape, or performance checks. Produce typed evidence rather than a prose summary.

### Stage 2: blinded discovery

Give hunters the public task, architecture brief, frozen repository, patch, and non-leading mechanical facts such as build availability. Do not reveal which private acceptance groups failed before initial discovery. This measures whether the reviewer can find a defect rather than follow the answer key.

### Stage 3: reconciliation and focused verification

After hunters freeze claims, reveal relevant deterministic outcomes. A verifier tries to reproduce or falsify every material finding and reconciles conflicts. An absence-of-findings result must include a coverage receipt: files/paths examined, requirements mapped, and checks attempted.

### Stage 4: deterministic criterion scoring

The rubric engine maps verified evidence to criterion levels, applies caps and hard gates, and calculates the 30-point semantic score. It rejects contradictions such as “high authorization bypass” plus full security points.

### Stage 5: pairwise overlay

For research leaderboards, compare close candidates per dimension in both orders. Keep this ranking separate from the absolute gate/score so historical results do not change merely because a new candidate entered the pool.

### Stage 6: grader monitoring

Log raw findings, rejected findings, dissent, evidence, criterion decisions, repeat samples, costs, and calibration metrics. Human-audit all critical/high claims, all close-to-threshold runs, all judge disagreements, and a random benign sample until validation shows a lower review rate is safe.

## 8. Practical migration plan

### Phase 1: make the existing score meaningful

1. Replace the five bare bounds with versioned criterion cards and explicit anchors.
2. Require per-criterion evidence, counterevidence, and `notAssessed` rather than silent guessing.
3. Remove the model-supplied total; calculate in C#.
4. Separate finding discovery from scoring, even if both initially use separate one-shot calls.
5. Blind discovery to private acceptance results; disclose them only during reconciliation.
6. Add finding-to-score consistency validation and retain raw decisions.

This is the best low-cost intervention and should happen before adding more models.

### Phase 2: prove the measurement

1. Hand-label the existing patches and a controlled defect/mutation set.
2. Run repeated current-vs-v2 comparisons.
3. Add ceiling, agreement, precision/recall, and variance reports.
4. Freeze promotion thresholds and a grader profile version.
5. Expand beyond the single search task.

### Phase 3: make review genuinely agentic

1. Allow a fixed read-only evidence agent to use repository search and focused test commands.
2. Add separate correctness and test-efficacy lenses first; they are the largest gaps in the current holistic call.
3. Add security and performance specialists only when activated by the task/risk profile.
4. Add a skeptic for material findings and no-finding coverage receipts.

### Phase 4: improve ranking and robustness

1. Add bidirectional pairwise comparisons for close candidates.
2. Test a diverse panel against the same human gold set.
3. Adopt a jury only if it improves validated precision/recall per unit cost.
4. Keep a human audit and an explicit “grader uncertain” outcome.

## 9. Decisions to avoid

- Do not simply switch to a larger judge model and retain the same rubric. That changes capability, not the missing measurement contract.
- Do not ask one agent to discover, verify, score, total, and narrate in one response.
- Do not make “no findings” synonymous with maximum points.
- Do not use raw code coverage as test-quality credit; Microsoft explicitly warns that coverage does not determine quality ([Microsoft .NET unit-test guidance](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)).
- Do not make a gold patch the required architecture.
- Do not average three correlated opinions and call the result confidence.
- Do not expose private failure labels before measuring independent defect discovery.
- Do not score personal clean-code preferences, line counts, pattern usage, or abstraction count without a concrete maintenance or risk consequence.
- Do not change the grader profile while comparing implementation models without either regrading all candidates or declaring a new benchmark version.

## Final answer to the architecture question

Use a **specialist grader agent for adaptive semantic investigation**, but keep the grader's constitution in code and versioned data. The specialist should return a typed evidence ledger; a separate verifier should challenge material claims; deterministic code should own applicability, caps, hard gates, weights, and arithmetic. Start with one evidence agent plus an anchored criterion rubric, validate it against human-labelled and mutated cases, and add specialists or a jury only when ablations show measurable gains.

That design solves both failure modes in the current harness: the rubric gains an operational meaning, and the reviewer gains the ability to establish evidence rather than infer a maximum score from a passing summary.
