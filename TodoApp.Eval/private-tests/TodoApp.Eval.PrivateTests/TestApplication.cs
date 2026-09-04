using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TodoApi;

namespace TodoApp.Eval.PrivateTests;

internal sealed class TestApplication : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("Filename=:memory:");

    public TodoDbContext Db()
    {
        var db = Services.GetRequiredService<IDbContextFactory<TodoDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
        return db;
    }

    public async Task AddUser(string id)
    {
        using var scope = Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<TodoUser>>();
        var result = await manager.CreateAsync(new TodoUser { Id = id, UserName = id }, "x");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    public HttpClient Authenticated(string id, bool admin = false) => CreateDefaultClient(new AuthHandler(Services, id, admin));

    protected override IHost CreateHost(IHostBuilder builder)
    {
        connection.Open();
        builder.ConfigureServices(services =>
        {
            services.AddDbContextFactory<TodoDbContext>();
            services.RemoveAll<DbContextOptions<TodoDbContext>>();
            var options = new DbContextOptionsBuilder<TodoDbContext>().UseSqlite(connection).Options;
            services.AddSingleton(options);
            services.AddSingleton<DbContextOptions>(options);
            services.Configure<IdentityOptions>(o =>
            {
                o.Password.RequireNonAlphanumeric = false; o.Password.RequireDigit = false;
                o.Password.RequiredUniqueChars = 0; o.Password.RequiredLength = 1;
                o.Password.RequireLowercase = false; o.Password.RequireUppercase = false;
            });
            services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
            services.AddScoped<TestTokenService>();
        });
        var host = base.CreateHost(builder);
        using (var db = host.Services.GetRequiredService<IDbContextFactory<TodoDbContext>>().CreateDbContext())
        {
            db.Database.EnsureCreated();
        }
        return host;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) connection.Dispose();
        base.Dispose(disposing);
    }

    private sealed class AuthHandler(IServiceProvider services, string id, bool admin) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await using var scope = services.CreateAsyncScope();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await scope.ServiceProvider.GetRequiredService<TestTokenService>().Generate(id, admin));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

internal sealed class TestTokenService(SignInManager<TodoUser> signInManager, IOptionsMonitor<BearerTokenOptions> options)
{
    private readonly BearerTokenOptions bearer = options.Get(IdentityConstants.BearerScheme);

    public async Task<string> Generate(string id, bool admin)
    {
        var principal = await signInManager.CreateUserPrincipalAsync(new TodoUser { Id = id, UserName = id });
        if (admin) ((ClaimsIdentity?)principal.Identity)?.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        var now = (bearer.TimeProvider ?? TimeProvider.System).GetUtcNow();
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = now + bearer.BearerTokenExpiration },
            $"{IdentityConstants.BearerScheme}:AccessToken");
        return bearer.BearerTokenProtector.Protect(ticket);
    }
}
