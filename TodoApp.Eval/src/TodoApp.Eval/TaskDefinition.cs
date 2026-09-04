namespace TodoApp.Eval;

internal static class TaskDefinition
{
    public const string PinnedCommit = "23ce718962f125cccfa25c4adcb7e7a14360d79a";
    public const int ExpectedBaselineTestCount = 16;

    public const string PublicTask = """
        # Todo search and pagination

        As a signed-in TodoApp user, I want to search and page through my own todos so that I can find relevant work without retrieving my entire todo list.

        Add `GET /todos/search` with:

        - `q`: optional title search; trim whitespace, treat empty as no filter, and match case-insensitively for Unicode text as well as ASCII.
        - `isComplete`: optional Boolean completion filter.
        - `page`: optional one-based page number, default `1`; reject values below `1`.
        - `pageSize`: optional size, default `20`; reject values outside `1–100`.
        - Invalid query values return `400` using the repository's normal Problem Details behavior.
        - Results contain only the current user's todos, including when that user is an administrator.
        - Filtering occurs before counting and pagination.
        - Results are ordered by ascending todo ID.
        - Pages beyond the available results return `200` with an empty `items` array.
        - The response JSON contains `items`, `totalCount`, `page`, and `pageSize`; each item retains the existing todo JSON shape.
        - The endpoint inherits existing todo authorization and rate limiting.
        - Unauthenticated callers receive `401`; authenticated identities without a corresponding database user receive `403`.
        - No database migration or new NuGet dependency is needed.
        - Add integration tests covering the new behavior.

        Implement the feature completely, run relevant tests, and leave all changes in this worktree. Do not commit. Do not access files outside this repository.
        """;

    public const string ArchitectureBrief = """
        TodoApp is a .NET 9 minimal-API application using EF Core SQLite, grouped todo routes,
        current-user authorization, per-user rate limiting, and xUnit WebApplicationFactory tests.
        Prefer a direct, query-efficient implementation consistent with existing patterns. Do not
        reward abstraction that has no concrete value for this task.
        """;
}
