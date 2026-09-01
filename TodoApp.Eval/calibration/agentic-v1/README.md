# Agentic grader v1 calibration pack

`cases.json` freezes nine known-answer fixture identities and their minimal assertions. Fixture identities are content-versioned variants of the pinned `todo-search-v1` base; the fixture store/materializer is intentionally separate from ordinary unit tests so normal builds never invoke a model or expose private acceptance outcomes.

For each model configuration, place the retained result for each case at `<results>/<case-id>/semantic-grade.json` with its `manifest.json`, then run:

```sh
dotnet run --project src/TodoApp.Eval -- calibration-check --results <results>
```

The verifier checks the model and effort recorded in every manifest, required and forbidden finding criteria, and criterion ceilings and floors. Use the same pack first with `gpt-5.6-terra`/`high`, then once with the temporary comparison:

```sh
dotnet run --project src/TodoApp.Eval -- calibration-check --results <sol-results> \
  --model gpt-5.6-sol --reasoning-effort high
```

Model promotion remains a deliberate experiment step; unit tests validate the pack and verifier without spending tokens.
