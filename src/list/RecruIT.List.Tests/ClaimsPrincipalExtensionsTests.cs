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
            new Claim("preferred_username", "jdoe"),
            new Claim(ClaimsPrincipalExtensions.EmailTrustedClaimType, "true")
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
            new Claim(ClaimTypes.Name, "Jane Doe"),
            new Claim(ClaimsPrincipalExtensions.EmailTrustedClaimType, "true")
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

    [Fact]
    public void An_email_the_sign_in_did_not_vouch_for_is_not_a_user_key()
    {
        var user = User(new Claim("sub", "jane-sub"), new Claim("email", "jane@example.com"));

        Assert.Equal("jane@example.com", user.GetEmail());
        Assert.Null(user.GetVerifiedEmail());
        Assert.Equal(("jane-sub", null), user.GetUserKeys());
    }

    [Fact]
    public void A_vouched_for_email_is_a_user_key()
    {
        var user = User(
            new Claim("sub", "jane-sub"),
            new Claim("email", "jane@example.com"),
            new Claim(ClaimsPrincipalExtensions.EmailTrustedClaimType, "true")
        );

        Assert.Equal(("jane-sub", "jane@example.com"), user.GetUserKeys());
    }
}
