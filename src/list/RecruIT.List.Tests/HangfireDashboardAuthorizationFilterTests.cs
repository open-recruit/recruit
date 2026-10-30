using System.Security.Claims;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RecruIT.List.Services.Notify;
using static RecruIT.List.Tests.TestUsers;

namespace RecruIT.List.Tests;

public sealed class HangfireDashboardAuthorizationFilterTests
{
    private static bool Authorize(bool authEnabled, ClaimsPrincipal user) =>
        new HangfireDashboardAuthorizationFilter(authEnabled).Authorize(
            new AspNetCoreDashboardContext(
                new UnusedJobStorage(),
                new DashboardOptions(),
                new DefaultHttpContext
                {
                    User = user,
                    RequestServices = new ServiceCollection().BuildServiceProvider(),
                }
            )
        );

    [Fact]
    public void Admins_may_open_the_dashboard() =>
        Assert.True(Authorize(authEnabled: true, CreateUser(sub: "admin-1", isAdmin: true)));

    [Fact]
    public void Other_signed_in_users_may_not() =>
        Assert.False(Authorize(authEnabled: true, CreateUser(sub: "user-1")));

    [Fact]
    public void Anonymous_visitors_may_not_even_with_an_admin_role_claim()
    {
        var unauthenticated = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim("role", "admin")],
                authenticationType: null,
                "name",
                "role"
            )
        );

        Assert.False(Authorize(authEnabled: true, unauthenticated));
    }

    [Fact]
    public void Everyone_may_when_authentication_is_disabled() =>
        Assert.True(Authorize(authEnabled: false, new ClaimsPrincipal()));

    /// <summary>The filter never touches storage; AspNetCoreDashboardContext just requires one.</summary>
    private sealed class UnusedJobStorage : JobStorage
    {
        public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();

        public override IStorageConnection GetConnection() => throw new NotSupportedException();
    }
}
