using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TodoApi;

namespace TodoApp.Eval.PrivateTests;

public sealed class SearchAcceptanceTests
{
    [Fact]
    public async Task AuthenticationIsolationRequiresAuthenticationAndDatabaseUser()
    {
        await using var app = new TestApplication();
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/todos/search")).StatusCode);
        using var missing = app.Authenticated("missing");
        Assert.Equal(HttpStatusCode.Forbidden, (await missing.GetAsync("/todos/search")).StatusCode);
    }

    [Fact]
    public async Task AuthenticationIsolationReturnsOnlyCurrentUsersItemsEvenForAdmin()
    {
        await using var app = new TestApplication(); await app.AddUser("owner"); await app.AddUser("other"); await app.AddUser("admin");
        await using var db = app.Db();
        db.Todos.AddRange(new Todo { Title = "match owner", OwnerId = "owner" }, new Todo { Title = "match other", OwnerId = "other" }, new Todo { Title = "match admin", OwnerId = "admin" });
        await db.SaveChangesAsync();
        var owner = await Get(app.Authenticated("owner"), "/todos/search?q=match");
        Assert.Equal(["match owner"], Titles(owner));
        var admin = await Get(app.Authenticated("admin", admin: true), "/todos/search?q=match");
        Assert.Equal(["match admin"], Titles(admin));
    }

    [Fact]
    public async Task AuthenticationIsolationAdminWithoutQueryReceivesOnlyOwnItemsAndCount()
    {
        await using var app = new TestApplication(); await app.AddUser("owner"); await app.AddUser("admin");
        await using var db = app.Db();
        db.Todos.AddRange(
            new Todo { Title = "owner only", OwnerId = "owner" },
            new Todo { Title = "admin first", OwnerId = "admin", IsComplete = false },
            new Todo { Title = "admin second", OwnerId = "admin", IsComplete = true });
        await db.SaveChangesAsync();

        var result = await Get(app.Authenticated("admin", admin: true), "/todos/search");

        Assert.Equal(2, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(["admin first", "admin second"], Titles(result));
    }

    [Fact]
    public async Task FilteringTrimsSearchAndMatchesCaseInsensitively()
    {
        await using var app = await Seed("u", new SeedTodo("Alpha WORK item", false), new SeedTodo("unrelated", false));
        var result = await Get(app.Authenticated("u"), "/todos/search?q=%20%20work%20%20");
        Assert.Equal(["Alpha WORK item"], Titles(result));
    }

    [Fact]
    public async Task FilteringMatchesNonAsciiCasing()
    {
        await using var app = await Seed("u", new SeedTodo("ÄRGER", false), new SeedTodo("unrelated", false));
        var result = await Get(app.Authenticated("u"), "/todos/search?q=%C3%A4rger");
        Assert.Equal(["ÄRGER"], Titles(result));
    }

    [Fact]
    public async Task FilteringEmptyQueryIsNoFilterAndCompletionSupportsBothValues()
    {
        await using var app = await Seed("u", new SeedTodo("open", false), new SeedTodo("done", true)); var client = app.Authenticated("u");
        Assert.Equal(2, Items(await Get(client, "/todos/search?q=%20%20")).GetArrayLength());
        Assert.Equal(["open"], Titles(await Get(client, "/todos/search?isComplete=false")));
        Assert.Equal(["done"], Titles(await Get(client, "/todos/search?isComplete=true")));
        Assert.Empty(Titles(await Get(client, "/todos/search?q=open&isComplete=true")));
    }

    [Fact]
    public async Task PaginationContractDefaultsShapeOrderAndEmptyResults()
    {
        await using var app = await Seed("u", new SeedTodo("one", false), new SeedTodo("two", true), new SeedTodo("three", false));
        var result = await Get(app.Authenticated("u"), "/todos/search");
        Assert.Equal(1, result.GetProperty("page").GetInt32()); Assert.Equal(20, result.GetProperty("pageSize").GetInt32()); Assert.Equal(3, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(["items", "page", "pageSize", "totalCount"], result.EnumerateObject().Select(x => x.Name).Order());
        var items = Items(result).EnumerateArray().ToArray();
        Assert.Equal(items.Select(x => x.GetProperty("id").GetInt32()).Order(), items.Select(x => x.GetProperty("id").GetInt32()));
        Assert.Collection(items,
            item => AssertItem(item, "one", false),
            item => AssertItem(item, "two", true),
            item => AssertItem(item, "three", false));
        var empty = await Get(app.Authenticated("u"), "/todos/search?q=absent"); Assert.Equal(0, empty.GetProperty("totalCount").GetInt32()); Assert.Equal(0, Items(empty).GetArrayLength());
    }

    [Fact]
    public async Task PaginationContractPagesAfterFilteringAndReportsPrePaginationCount()
    {
        await using var app = await Seed("u", new SeedTodo("hit 1", false), new SeedTodo("miss", false), new SeedTodo("hit 2", false), new SeedTodo("hit 3", false), new SeedTodo("hit 4", true)); var client = app.Authenticated("u");
        var page1 = await Get(client, "/todos/search?q=hit&isComplete=false&page=1&pageSize=2");
        Assert.Equal(3, page1.GetProperty("totalCount").GetInt32()); Assert.Equal(["hit 1", "hit 2"], Titles(page1));
        var page2 = await Get(client, "/todos/search?q=hit&isComplete=false&page=2&pageSize=2"); Assert.Equal(3, page2.GetProperty("totalCount").GetInt32()); Assert.Equal(["hit 3"], Titles(page2));
        var page9 = await Get(client, "/todos/search?q=hit&page=9&pageSize=2"); Assert.Equal(4, page9.GetProperty("totalCount").GetInt32()); Assert.Equal(0, Items(page9).GetArrayLength());
    }

    [Fact]
    public async Task PaginationContractMaximumPageValueReturnsEmptyPageWithoutOverflow()
    {
        await using var app = await Seed("u", new SeedTodo("one", false));

        var result = await Get(app.Authenticated("u"), $"/todos/search?page={int.MaxValue}&pageSize=100");

        Assert.Equal(int.MaxValue, result.GetProperty("page").GetInt32());
        Assert.Equal(100, result.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, result.GetProperty("totalCount").GetInt32());
        Assert.Empty(Items(result).EnumerateArray());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=nope")]
    [InlineData("pageSize=nope")]
    [InlineData("isComplete=nope")]
    public async Task ValidationRegressionInvalidValuesReturnFieldSpecificProblemDetails(string query)
    {
        await using var app = await Seed("u", new SeedTodo("one", false));
        var response = await app.Authenticated("u").GetAsync("/todos/search?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        var expectedKey = query[..query.IndexOf('=')];
        var error = Assert.Single(problem.Errors, x => string.Equals(x.Key, expectedKey, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(error.Value, message => !string.IsNullOrWhiteSpace(message));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task ValidationRegressionValidPageSizeBoundariesReturnRequestedNumberOfItems(int pageSize)
    {
        var todos = Enumerable.Range(1, 101).Select(index => new SeedTodo($"todo {index:D3}", index % 2 == 0)).ToArray();
        await using var app = await Seed("u", todos);
        await using var db = app.Db();
        var expectedIds = db.Todos.Where(x => x.OwnerId == "u").OrderBy(x => x.Id).Take(pageSize).Select(x => x.Id).ToArray();

        var result = await Get(app.Authenticated("u"), $"/todos/search?page=1&pageSize={pageSize}");

        Assert.Equal(1, result.GetProperty("page").GetInt32());
        Assert.Equal(pageSize, result.GetProperty("pageSize").GetInt32());
        Assert.Equal(101, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(pageSize, Items(result).GetArrayLength());
        Assert.Equal(expectedIds, Items(result).EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task ValidationRegressionExistingTodoEndpointsStillWork()
    {
        await using var app = await Seed("u", new SeedTodo("existing", false)); var client = app.Authenticated("u");
        var existing = await client.GetFromJsonAsync<List<TodoItem>>("/todos"); Assert.Single(existing!);
        var created = await client.PostAsJsonAsync("/todos", new TodoItem { Title = "new" }); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private static async Task<TestApplication> Seed(string user, params SeedTodo[] todos)
    {
        var app = new TestApplication(); await app.AddUser(user); await using var db = app.Db();
        db.Todos.AddRange(todos.Select(x => new Todo { Title = x.Title, IsComplete = x.IsComplete, OwnerId = user })); await db.SaveChangesAsync(); return app;
    }
    private static async Task<JsonElement> Get(HttpClient client, string uri)
    {
        var response = await client.GetAsync(uri); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync())).RootElement.Clone();
    }
    private static JsonElement Items(JsonElement result) => result.GetProperty("items");
    private static string[] Titles(JsonElement result) => Items(result).EnumerateArray().Select(x => x.GetProperty("title").GetString()!).ToArray();
    private static void AssertItem(JsonElement item, string title, bool isComplete)
    {
        Assert.Equal(["id", "isComplete", "title"], item.EnumerateObject().Select(x => x.Name).Order());
        Assert.True(item.GetProperty("id").GetInt32() > 0);
        Assert.Equal(title, item.GetProperty("title").GetString());
        Assert.Equal(isComplete, item.GetProperty("isComplete").GetBoolean());
    }
    private sealed record SeedTodo(string Title, bool IsComplete);
}
