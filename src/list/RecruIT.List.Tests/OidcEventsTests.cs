using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RecruIT.List.Models;
using RecruIT.List.Services.Auth;
using static RecruIT.List.Tests.TestUsers;

namespace RecruIT.List.Tests;

/// <summary>What a real sign-in adds to the session: mapped group roles and activated grants.</summary>
public sealed class OidcEventsTests
{
    private static readonly TrialIdentifier Trial = new(
        "https://fhir.example.org/study-id",
        "STUDY-A"
    );

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                settings.Select(s => KeyValuePair.Create($"Auth:Oidc:{s.Key}", (string?)s.Value))
            )
            .Build();

    private static IConfiguration RequireVerifiedEmail(bool required) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([
                KeyValuePair.Create("Auth:RequireVerifiedEmail", (string?)required.ToString()),
            ])
            .Build();

    private static ClaimsPrincipal SignedIn(string roleClaimType, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "oidc", "preferred_username", roleClaimType));

    private static async Task SignInAsync(
        IConfiguration configuration,
        SqliteDbContextFactory factory,
        ClaimsPrincipal principal
    )
    {
        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("oidc", null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            principal,
            new AuthenticationProperties()
        );
        await new OidcEvents(
            configuration,
            factory,
            NullLogger<OidcEvents>.Instance
        ).TokenValidated(context);
    }

    [Fact]
    public async Task Mapped_groups_become_roles()
    {
        using var factory = new SqliteDbContextFactory();
        var user = SignedIn(
            "role",
            new Claim("groups", "/recruit-admins"),
            new Claim("groups", "/unrelated")
        );

        await SignInAsync(
            Configuration(("GroupRoleMappings:/recruit-admins", "admin")),
            factory,
            user
        );

        Assert.True(user.IsInRole("admin"));
        Assert.Equal(["admin"], user.FindAll("role").Select(c => c.Value));
    }

    [Fact]
    public async Task A_role_the_user_already_has_is_not_added_twice()
    {
        using var factory = new SqliteDbContextFactory();
        var user = SignedIn(
            "role",
            new Claim("groups", "/recruit-admins"),
            new Claim("role", "admin")
        );

        await SignInAsync(
            Configuration(("GroupRoleMappings:/recruit-admins", "admin")),
            factory,
            user
        );

        Assert.Single(user.FindAll("role"));
    }

    [Fact]
    public async Task Custom_group_and_role_claim_types_are_honored()
    {
        using var factory = new SqliteDbContextFactory();
        var user = SignedIn("realm_role", new Claim("memberOf", "recruit-admins"));

        await SignInAsync(
            Configuration(
                ("GroupClaimType", "memberOf"),
                ("RoleClaimType", "realm_role"),
                ("GroupRoleMappings:recruit-admins", "admin")
            ),
            factory,
            user
        );

        Assert.True(user.IsInRole("admin"));
    }

    [Fact]
    public async Task Grants_created_by_email_are_activated_for_the_signed_in_user()
    {
        using var factory = new SqliteDbContextFactory();
        await SeedGrantAsync(factory, Trial, "jane@example.com", TrialPermissionLevel.Viewer);
        await SeedGrantAsync(factory, Trial, "other@example.com", TrialPermissionLevel.Viewer);

        await SignInAsync(
            Configuration(),
            factory,
            SignedIn(
                "role",
                new Claim("sub", "jane-sub"),
                new Claim("email", "jane@example.com"),
                new Claim("email_verified", "true"),
                new Claim("name", "Jane Doe")
            )
        );

        await using var db = factory.CreateDbContext();
        var grants = await db
            .TrialAccessGrants.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
        var janes = Assert.Single(grants, g => g.Email == "jane@example.com");
        Assert.Equal(("jane-sub", "Jane Doe"), (janes.SubjectId, janes.DisplayName));
        Assert.Null(Assert.Single(grants, g => g.Email == "other@example.com").SubjectId);
    }

    [Fact]
    public async Task A_grant_already_bound_to_a_subject_is_left_alone()
    {
        using var factory = new SqliteDbContextFactory();
        await SeedGrantAsync(
            factory,
            Trial,
            "jane@example.com",
            TrialPermissionLevel.Viewer,
            subjectId: "original-sub"
        );

        await SignInAsync(
            Configuration(),
            factory,
            SignedIn(
                "role",
                new Claim("sub", "new-sub"),
                new Claim("email", "jane@example.com"),
                new Claim("email_verified", "true")
            )
        );

        await using var db = factory.CreateDbContext();
        Assert.Equal(
            "original-sub",
            (
                await db.TrialAccessGrants.SingleAsync(TestContext.Current.CancellationToken)
            ).SubjectId
        );
    }

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public async Task An_unverified_email_does_not_claim_grants_given_to_that_address(
        string? emailVerified
    )
    {
        using var factory = new SqliteDbContextFactory();
        await SeedGrantAsync(factory, Trial, "victim@example.com", TrialPermissionLevel.TrialAdmin);
        var attacker = SignedIn(
            "role",
            [
                new Claim("sub", "attacker-sub"),
                new Claim("email", "victim@example.com"),
                .. emailVerified is null
                    ? []
                    : new[] { new Claim("email_verified", emailVerified) },
            ]
        );

        await SignInAsync(Configuration(), factory, attacker);

        await using var db = factory.CreateDbContext();
        Assert.Null(
            (
                await db.TrialAccessGrants.SingleAsync(TestContext.Current.CancellationToken)
            ).SubjectId
        );
        Assert.Null(attacker.GetVerifiedEmail());
        Assert.Equal(("attacker-sub", null), attacker.GetUserKeys());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public async Task A_verified_email_is_trusted_however_the_idp_spells_true(string emailVerified)
    {
        using var factory = new SqliteDbContextFactory();
        var user = SignedIn(
            "role",
            new Claim("sub", "jane-sub"),
            new Claim("email", "jane@example.com"),
            new Claim("email_verified", emailVerified)
        );

        await SignInAsync(Configuration(), factory, user);

        Assert.Equal("jane@example.com", user.GetVerifiedEmail());
    }

    [Fact]
    public async Task A_deployment_may_accept_unverified_emails_for_an_idp_without_the_claim()
    {
        using var factory = new SqliteDbContextFactory();
        await SeedGrantAsync(factory, Trial, "jane@example.com", TrialPermissionLevel.Viewer);
        var user = SignedIn(
            "role",
            new Claim("sub", "jane-sub"),
            new Claim("email", "jane@example.com")
        );

        await SignInAsync(RequireVerifiedEmail(false), factory, user);

        Assert.Equal("jane@example.com", user.GetVerifiedEmail());
        await using var db = factory.CreateDbContext();
        Assert.Equal(
            "jane-sub",
            (
                await db.TrialAccessGrants.SingleAsync(TestContext.Current.CancellationToken)
            ).SubjectId
        );
    }

    [Fact]
    public async Task A_trust_claim_arriving_in_the_token_is_not_believed()
    {
        using var factory = new SqliteDbContextFactory();
        var user = SignedIn(
            "role",
            new Claim("sub", "attacker-sub"),
            new Claim("email", "victim@example.com"),
            new Claim("email_verified", "false"),
            new Claim(ClaimsPrincipalExtensions.EmailTrustedClaimType, "true")
        );

        await SignInAsync(Configuration(), factory, user);

        Assert.Null(user.GetVerifiedEmail());
        Assert.Empty(user.FindAll(ClaimsPrincipalExtensions.EmailTrustedClaimType));
    }
}
