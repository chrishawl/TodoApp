using Microsoft.AspNetCore.Components.Authorization;

namespace Todo.Web.Server;

internal sealed class HttpAuthenticationStateProvider(IHttpContextAccessor httpContextAccessor) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        return Task.FromResult(new AuthenticationState(httpContextAccessor.HttpContext?.User ?? new()));
    }
}
