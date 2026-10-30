using System.Security.Claims;
using RecruIT.List.Services.Auth;

namespace RecruIT.List.Tests;

public sealed class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal User(params Claim[] claims) =>
        new(
            new ClaimsIdentity(
                claims,
                authenticationType: "Test",
                nameType: "preferred_username",
                roleType: "role"
            )
        );

    [Fact]
    public void Prefers_the_oidc_claims()
    {
        var user = User(
            new Claim("sub", "oidc-sub"),
            new Claim(ClaimTypes.NameIdentifier, "other-sub"),
            new Claim("email", "jane@example.com"),
            new Claim(ClaimTypes.Email, "other@example.com"),
            new Claim("name", "Jane Doe"),
            new Claim("preferred_username", "jdoe")
        );

        Assert.Equal(("oidc-sub", "jane@example.com"), user.GetUserKeys());
        Assert.Equal("Jane Doe", user.GetDisplayName());
    }

    [Fact]
    public void Falls_back_to_the_standard_claim_types()
    {
        var user = User(
            new Claim(ClaimTypes.NameIdentifier, "standard-sub"),
            new Claim(ClaimTypes.Email, "jane@example.com"),
            new Claim(ClaimTypes.Name, "Jane Doe")
        );

        Assert.Equal(("standard-sub", "jane@example.com"), user.GetUserKeys());
        Assert.Equal("Jane Doe", user.GetDisplayName());
    }

    [Fact]
    public void Display_name_falls_back_to_the_login_handle() =>
        Assert.Equal("jdoe", User(new Claim("preferred_username", "jdoe")).GetDisplayName());

    [Fact]
    public void Everything_is_null_for_an_anonymous_user()
    {
        var anonymous = new ClaimsPrincipal();

        Assert.Equal((null, null), anonymous.GetUserKeys());
        Assert.Null(anonymous.GetDisplayName());
    }
}
