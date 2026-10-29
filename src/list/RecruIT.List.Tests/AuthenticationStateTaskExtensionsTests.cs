using Microsoft.AspNetCore.Components.Authorization;
using RecruIT.List.Services.Auth;
using static RecruIT.List.Tests.TestUsers;

namespace RecruIT.List.Tests;

public sealed class AuthenticationStateTaskExtensionsTests
{
    [Fact]
    public async Task GetUserAsync_ReturnsTheCascadedUser()
    {
        var user = CreateUser(sub: "alice-sub", email: "alice@example.com");

        var resolved = await Task.FromResult(new AuthenticationState(user)).GetUserAsync();

        Assert.Same(user, resolved);
    }

    [Fact]
    public async Task GetUserAsync_IsAnonymousWithoutACascadedState()
    {
        Task<AuthenticationState>? missing = null;

        var resolved = await missing.GetUserAsync();

        Assert.False(resolved.Identity?.IsAuthenticated ?? false);
        Assert.Empty(resolved.Claims);
    }
}
