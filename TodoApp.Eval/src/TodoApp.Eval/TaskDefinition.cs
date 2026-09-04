namespace TodoApp.Eval;

internal static class TaskDefinition
{
    public const string PinnedCommit = "549a3ece7a6c2f5bcf351bf6c5b08132f534ebc9";
    public const int ExpectedBaselineTestCount = 16;

    public const string PublicTask = """
        # Todo search and pagination

        As a signed-in TodoApp user, I want to search and page through my own todos so that I can find relevant work without retrieving my entire todo list.

        Add `GET /todos/search` with:

        - `q`: optional title search; trim whitespace, treat empty as no filter, and match case-insensitively for Unicode text as well as ASCII.
        - `isComplete`: optional Boolean completion filter.
        - `page`: optional one-based page number, default `1`; reject values below `1`.
        - `pageSize`: optional size, default `20`; reject values outside `1–100`.
        - Invalid query values return `400` using the repository's normal validation Problem Details behavior, including a useful field-specific error for each invalid query parameter.
        - Results and `totalCount` contain only the current user's todos for every filter combination, including an empty `q` and when that user is an administrator.
        - Filtering occurs before counting and pagination.
        - Results are ordered by ascending todo ID after filtering and before pagination.
        - The valid `pageSize` boundaries, `1` and `100`, are accepted and echoed exactly; when enough filtered results exist, `items` contains exactly the requested number.
        - Pages beyond the available results, including `page=2147483647`, return `200` with an empty `items` array and preserve the requested pagination metadata without arithmetic overflow.
        - The response JSON contains exactly `items`, `totalCount`, `page`, and `pageSize`; each item contains exactly the existing `id`, `title`, and `isComplete` JSON shape and values.
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
