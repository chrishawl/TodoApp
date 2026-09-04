using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TodoApi;

namespace TodoApp.Eval.PrivateTests;

public sealed class SearchAcceptanceTests
{
    [Fact]
    public async Task authenticationIsolation_requires_authentication_and_database_user()
    {
        await using var app = new TestApplication();
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/todos/search")).StatusCode);
        using var missing = app.Authenticated("missing");
        Assert.Equal(HttpStatusCode.Forbidden, (await missing.GetAsync("/todos/search")).StatusCode);
    }

    [Fact]
    public async Task authenticationIsolation_returns_only_current_users_items_even_for_admin()
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
    public async Task filtering_trims_search_and_matches_case_insensitively()
    {
        await using var app = await Seed("u", new SeedTodo("Alpha WORK item", false), new SeedTodo("unrelated", false));
        var result = await Get(app.Authenticated("u"), "/todos/search?q=%20%20work%20%20");
        Assert.Equal(["Alpha WORK item"], Titles(result));
    }

    [Fact]
    public async Task filtering_matches_non_ascii_casing()
    {
        await using var app = await Seed("u", new SeedTodo("ÄRGER", false), new SeedTodo("unrelated", false));
        var result = await Get(app.Authenticated("u"), "/todos/search?q=%C3%A4rger");
        Assert.Equal(["ÄRGER"], Titles(result));
    }

    [Fact]
    public async Task filtering_empty_query_is_no_filter_and_completion_supports_both_values()
    {
        await using var app = await Seed("u", new SeedTodo("open", false), new SeedTodo("done", true)); var client = app.Authenticated("u");
        Assert.Equal(2, Items(await Get(client, "/todos/search?q=%20%20")).GetArrayLength());
        Assert.Equal(["open"], Titles(await Get(client, "/todos/search?isComplete=false")));
        Assert.Equal(["done"], Titles(await Get(client, "/todos/search?isComplete=true")));
        Assert.Empty(Titles(await Get(client, "/todos/search?q=open&isComplete=true")));
    }

    [Fact]
    public async Task paginationContract_defaults_shape_order_and_empty_results()
    {
        await using var app = await Seed("u", new SeedTodo("one", false), new SeedTodo("two", false), new SeedTodo("three", false));
        var result = await Get(app.Authenticated("u"), "/todos/search");
        Assert.Equal(1, result.GetProperty("page").GetInt32()); Assert.Equal(20, result.GetProperty("pageSize").GetInt32()); Assert.Equal(3, result.GetProperty("totalCount").GetInt32());
        var items = Items(result).EnumerateArray().ToArray();
        Assert.Equal(items.Select(x => x.GetProperty("id").GetInt32()).Order(), items.Select(x => x.GetProperty("id").GetInt32()));
        foreach (var item in items) { Assert.True(item.TryGetProperty("id", out _)); Assert.True(item.TryGetProperty("title", out _)); Assert.True(item.TryGetProperty("isComplete", out _)); Assert.False(item.TryGetProperty("ownerId", out _)); }
        var empty = await Get(app.Authenticated("u"), "/todos/search?q=absent"); Assert.Equal(0, empty.GetProperty("totalCount").GetInt32()); Assert.Equal(0, Items(empty).GetArrayLength());
    }

    [Fact]
    public async Task paginationContract_pages_after_filtering_and_reports_pre_pagination_count()
    {
        await using var app = await Seed("u", new SeedTodo("hit 1", false), new SeedTodo("miss", false), new SeedTodo("hit 2", false), new SeedTodo("hit 3", false), new SeedTodo("hit 4", true)); var client = app.Authenticated("u");
        var page1 = await Get(client, "/todos/search?q=hit&isComplete=false&page=1&pageSize=2");
        Assert.Equal(3, page1.GetProperty("totalCount").GetInt32()); Assert.Equal(2, Items(page1).GetArrayLength());
        var page2 = await Get(client, "/todos/search?q=hit&isComplete=false&page=2&pageSize=2"); Assert.Equal(3, page2.GetProperty("totalCount").GetInt32()); Assert.Single(Items(page2).EnumerateArray());
        var page9 = await Get(client, "/todos/search?q=hit&page=9&pageSize=2"); Assert.Equal(4, page9.GetProperty("totalCount").GetInt32()); Assert.Equal(0, Items(page9).GetArrayLength());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=nope")]
    [InlineData("pageSize=nope")]
    [InlineData("isComplete=nope")]
    public async Task validationRegression_invalid_values_return_problem_details(string query)
    {
        await using var app = await Seed("u", new SeedTodo("one", false));
        var response = await app.Authenticated("u").GetAsync("/todos/search?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<ProblemDetails>());
    }

    [Theory]
    [InlineData("page=1&pageSize=1")]
    [InlineData("pageSize=100")]
    public async Task validationRegression_boundaries_are_valid(string query)
    {
        await using var app = await Seed("u", new SeedTodo("one", false));
        Assert.Equal(HttpStatusCode.OK, (await app.Authenticated("u").GetAsync("/todos/search?" + query)).StatusCode);
    }

    [Fact]
    public async Task validationRegression_existing_todo_endpoints_still_work()
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
    private sealed record SeedTodo(string Title, bool IsComplete);
}
