# Cost-effective agentic grader v1

Date: 2026-09-01

## Decision

Build one read-only, tool-using semantic grader. Keep rubric policy and score calculation deterministic.

The grader will inspect the completed worktree, trace relevant code, optionally run a small number of focused checks, and return typed evidence for a fixed rubric. It will not choose weights or calculate its own total. Remove the separate overall-narrative model call; the one grader response supplies the summary and recommendations, and C# renders the final report.

Start with `gpt-5.6-terra` at `high` reasoning. Official OpenAI guidance positions Terra as the balance of intelligence and cost; the higher reasoning setting is preferred here for semantic defect discovery. Keep `sol/high` only as a temporary comparison configuration while validating v1. See [OpenAI model guidance](https://developers.openai.com/api/docs/guides/latest-model).

## Why this is small enough

- One grader agent, not a panel.
- One model call per candidate, replacing the current review and narrative calls.
- Fifteen small rubric criteria, stored in one profile.
- Existing deterministic build, tests, analyzers, coverage, and dependency checks remain unchanged.
- Six to ten known-answer calibration cases; no large human-labelling project.
- No pairwise tournaments, juries, skeptic agents, mutation platform, or prompt optimizer in v1.

## Principles represented in the rubric

Dave Farley's *Modern Software Engineering* treats software engineering as learning through feedback and experimentation while managing complexity. The rubric therefore values observable behavior, rapid and meaningful test feedback, bounded complexity, and designs that make future change safer. See the [publisher's book overview and contents](https://www.oreilly.com/library/view/modern-software-engineering/9780137314942/).

Robert C. Martin's *Clean Code* emphasizes readability, intention-revealing names, coherent functions and classes, unobscured error handling, testing discipline, and maintainable dependency/design choices. These are evidence for maintainability; they are not rigid rules about line counts, function sizes, patterns, or abstraction. See the [publisher's Clean Code overview](https://www.informit.com/store/clean-code-a-handbook-of-agile-software-craftsmanship-9780132350884) and [second-edition overview](https://www.informit.com/store/clean-code-a-handbook-of-agile-software-craftsmanship-9780135398579).

The grader must always prefer task intent and concrete consequences over aesthetic preference. A direct solution can earn full credit. More abstraction does not mean better architecture.

## Rubric: 30 semantic points

Use 15 criteria worth two points each. Every criterion verdict is `met`, `partial`, `notMet`, or `notApplicable`.

- `met`: 2 points and affirmative evidence.
- `partial`: 1 point with the gap identified.
- `notMet`: 0 points with a concrete finding.
- `notApplicable`: excluded from the denominator by the task profile; the core normalizes the applicable score to 30.

The task profile, not the agent, determines applicability. A high-severity finding forces its linked criterion to `notMet`; a medium finding caps it at `partial`. No finding is not evidence that a criterion is met.

| Attribute | Points | Criteria |
| --- | ---: | --- |
| Solution intent and functional fit | 8 | Requested behavior is complete; solution is reachable through the real entry point; task constraints are honoured; important untested boundaries agree with intent |
| Simplicity and complexity management | 4 | Design is the simplest sufficient solution; control/data flow avoids incidental complexity and speculative generality |
| Architecture and change locality | 4 | Responsibilities sit in the appropriate module and follow local conventions; dependencies, coupling, and change impact are contained |
| Readability and maintainability | 4 | Names and structure reveal intent without explanatory noise; invariants, errors, and duplication are handled clearly enough for safe modification |
| Test quality and feedback | 4 | Tests exercise useful behavior through appropriate interfaces; negative/boundary assertions would detect plausible regressions and remain deterministic/readable |
| Reliability and edge safety | 2 | Relevant boundaries, invalid states, failure behavior, overflow, cancellation, or concurrency risks are handled proportionately |
| Security and data protection | 2 | Relevant authentication, authorization, isolation, validation, and sensitive-data paths are preserved |
| Performance efficiency | 2 | Relevant work is bounded and occurs in the correct layer without avoidable round trips, full materialization, or algorithmic waste |
| **Total** | **30** | **15 criteria** |

For `todo-search-v1`, all 15 criteria are applicable. The profile should include specific risk reminders for owner scoping, administrator isolation, SQLite case folding, filtering before count/paging, page-offset overflow, server-side query execution, authorization inheritance, and test falsification.

## Small module and interface

Create a `SemanticGrading` module with one external interface:

```csharp
Task<SemanticGrade> GradeAsync(GradingCase candidate, CancellationToken cancellationToken);
```

`GradingCase` contains frozen evaluator-owned inputs:

- public task and architecture brief;
- worktree and patch identity;
- changed-file inventory;
- compact mechanical evidence;
- rubric-profile ID and hash.

`SemanticGrade` contains:

- exactly one verdict per applicable criterion;
- typed evidence references;
- evidence-backed findings with severity, trigger, impact, file, and line;
- files and paths inspected (the coverage receipt);
- concise strengths, failures, and recommendations;
- the C#-calculated semantic score.

The implementation hides prompt construction, agent execution, JSON parsing, evidence validation, criterion caps, and arithmetic behind this seam. Tests use the same interface with a fake executor.

## Agent behavior and cost budget

The grader receives the rubric and a compact evidence summary, not the complete serialized `DeterministicResult`. Initial discovery must not include private acceptance-group pass/fail labels.

The prompt instructs the grader to:

1. Map each rubric criterion to the changed code and relevant callers.
2. Inspect the changed files, tests, and the minimum surrounding context needed.
3. Investigate concrete risks rather than general style preferences.
4. Run focused read-only verification only where source inspection is insufficient.
5. Return the fixed JSON schema and stop.

Initial budget:

- model: `gpt-5.6-terra`;
- reasoning: `high`;
- one agent invocation;
- five-minute grader timeout;
- target at most eight tool calls and two focused test commands;
- concise JSON output with no duplicated patch or deterministic evidence;
- prompt/profile prefix kept stable so provider caching can be used where available.

Record tokens, calls, commands, wall time, and cost inputs. A budget overrun is a grader-health warning, not a candidate deduction.

## Implementation increments

### 1. Rubric and deterministic scorer

- Add `review-profiles/agentic-v1/rubric.json`, `prompt.md`, and the output JSON schema.
- Add criterion, verdict, evidence, and finding records.
- Add deterministic score calculation, applicability normalization, and severity caps.
- Unit-test missing criteria, duplicate criteria, invalid evidence, arithmetic, and high/medium caps.
- Remove model-owned `scores.total` from the contract.

Deliverable: fixed grader JSON can be validated and scored without calling a model.

### 2. Single agentic grader

- Adapt the existing `JudgeRunner` into the `SemanticGrading` module.
- Give the existing read-only agent access to the worktree and focused test commands.
- Replace the large deterministic JSON prompt with the compact discovery evidence view.
- Require a coverage receipt and evidence for every `met` or `notMet` verdict.
- Treat candidate instructions and comments as untrusted evidence.

Deliverable: one tool-using agent produces a valid `SemanticGrade` input.

### 3. Simplify orchestration and reporting

- Calculate all rubric points in C#.
- Remove `OverallAsync` and the second model call.
- Build the report narrative from the grader's typed summary, findings, and recommendations.
- Record the rubric/profile hash, model, effort, budget, and telemetry in the manifest.
- Keep deterministic acceptance points and hard gates authoritative.

Deliverable: one semantic call per evaluation with an auditable 30-point breakdown.

### 4. Lightweight calibration and model choice

Create six to ten frozen known-answer cases, not a large labelled dataset:

- no implementation;
- sound direct implementation;
- Unicode/SQLite case-folding defect;
- cross-user or administrator data leak;
- filter/count/pagination ordering defect;
- full-list materialization or unbounded query;
- weak or tautological submitted tests;
- correct but unnecessarily elaborate design;
- benign unfamiliar implementation as a false-positive control.

Each case needs only `mustFind`, `mustNotFind`, and criterion ceiling/floor assertions. It does not need a human-authored score for every attribute.

Run the pack against `terra/high` and the existing `sol/high` once during development. Choose Terra unless Sol materially improves known-defect recall or false-positive control. Optionally try `luna/medium` later; adopt it only if it passes the same pack.

Deliverable: automated evidence that the new grader separates known-good and known-bad cases without requiring ongoing human labelling.

## Definition of done

- Exactly one semantic model call is made per candidate.
- The grader inspects repository context and can run focused verification.
- C# owns all weights, applicability, caps, hard gates, and arithmetic.
- Every applicable criterion has a verdict and traceable evidence.
- A high finding cannot coexist with full points for its criterion.
- Passing private tests does not automatically yield a high semantic score.
- The known-answer calibration pack passes for the selected model/effort.
- The score report shows all 15 criteria and explains every lost point.
- Existing deterministic and report tests continue to pass.

## Explicitly deferred

- specialist panels, voting, debate, and a separate arbiter;
- pairwise candidate ranking;
- extensive human labels or inter-rater studies;
- automated prompt optimization;
- model diversity;
- full mutation testing infrastructure;
- generic repository-wide architecture scoring unrelated to the task.

Reconsider a second specialist only if the calibration pack shows a repeated blind spot that one better rubric and one evidence agent cannot solve.
