using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RecruIT.List.Services.Access;
using RecruIT.List.Services.Auth;

namespace RecruIT.List.Tests;

// Reads and, in one test, sets the TrialAccessService.AdminRoles static - see TrialAccessServiceTests.
[Collection(nameof(TrialAccessRoleCollection))]
public sealed class DevBypassAuthenticationHandlerTests
{
    private static async Task<AuthenticateResult> AuthenticateAsync()
    {
        var handler = new DevBypassAuthenticationHandler(
            new StaticOptionsMonitor(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default
        );
        await handler.InitializeAsync(
            new AuthenticationScheme(
                DevBypassAuthenticationHandler.SchemeName,
                null,
                typeof(DevBypassAuthenticationHandler)
            ),
            new DefaultHttpContext()
        );
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task Every_request_is_a_signed_in_admin()
    {
        var result = await AuthenticateAsync();

        Assert.True(result.Succeeded);
        var user = result.Principal;
        Assert.True(user.Identity?.IsAuthenticated);
        Assert.True(TrialAccessService.IsAdmin(user));
        Assert.Equal(("dev-admin", "dev-admin@localhost"), user.GetUserKeys());
        Assert.Equal("dev-admin", user.GetDisplayName());
    }

    [Fact]
    public async Task The_dev_admin_holds_every_configured_admin_role()
    {
        var originalAdminRoles = TrialAccessService.AdminRoles;
        try
        {
            TrialAccessService.AdminRoles = ["recruit-admin", "study-admin"];

            var user = (await AuthenticateAsync()).Principal!;

            Assert.True(user.IsInRole("recruit-admin"));
            Assert.True(user.IsInRole("study-admin"));
        }
        finally
        {
            TrialAccessService.AdminRoles = originalAdminRoles;
        }
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();

        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
