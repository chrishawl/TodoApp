# Agentic v2 calibration corpus

Each fixture is a frozen task, repository, and patch identity with human-agreed effective criterion ranges, dimension ranges, findings, and nearest-contrast ordering. Run the semantic grader three times per fixture and retain each result as:

```text
<results>/<case-id>/run-1/{semantic-grade.json,manifest.json}
<results>/<case-id>/run-2/{semantic-grade.json,manifest.json}
<results>/<case-id>/run-3/{semantic-grade.json,manifest.json}
```

`calibration-check` performs no model calls. It verifies the profile/configuration identity and the promotion thresholds documented in the repository README. The perturbation fixtures differ only by semantics-preserving renaming, formatting, comments, or source order; they share `strong-correct` as their stability baseline.
