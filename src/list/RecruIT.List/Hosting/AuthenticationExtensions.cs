using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using RecruIT.List.Options;
using RecruIT.List.Services.Access;
using RecruIT.List.Services.Auth;

namespace RecruIT.List.Hosting;

internal static class AuthenticationExtensions
{
    public static WebApplicationBuilder AddAuthentication(
        this WebApplicationBuilder builder,
        bool authEnabled,
        AdminRoles adminRoles
    )
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddAuthorizationCore(options =>
        {
            options.AddPolicy(AuthPolicies.Admin, policy => policy.RequireRole(adminRoles.Roles));
        });

        if (authEnabled)
        {
            builder.AddOpenIdConnect();
        }
        else
        {
            builder
                .Services.AddAuthentication(DevBypassAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevBypassAuthenticationHandler>(
                    DevBypassAuthenticationHandler.SchemeName,
                    _ => { }
                );
        }

        builder.Services.AddAuthorization();
        return builder;
    }

    private static void AddOpenIdConnect(this WebApplicationBuilder builder)
    {
        var oidcSection = builder.Configuration.GetSection(AuthOptions.OidcSectionName);
        var roleClaimType = oidcSection["RoleClaimType"] is { Length: > 0 } rc ? rc : "role";

        builder.Services.AddTransient<OidcEvents>();

        builder
            .Services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                options.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie()
            .AddOpenIdConnect(options =>
            {
                oidcSection.Bind(options);
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.SaveTokens = true;
                options.MapInboundClaims = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                // Not among the claims ASP.NET Core maps from the userinfo response by default; the
                // ID token usually carries it, but an IdP may only put it in the userinfo response.
                options.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");
                // Only relax to plain-HTTP metadata/issuer for local development (e.g. a local Keycloak
                // without TLS) - production authorities must always be HTTPS.
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
                // Opt-in only: ASP.NET Core defaults to using Pushed Authorization Requests whenever the
                // IdP advertises the endpoint, which added an extra failure mode against this app's
                // Keycloak setup for no real benefit at this app's size.
                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.Scope.Add("offline_access");
                options.TokenValidationParameters.RoleClaimType = roleClaimType;
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                options.EventsType = typeof(OidcEvents);
            });
    }

    /// <summary>The login and logout endpoints - only meaningful when OIDC authentication is on.</summary>
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder app)
    {
        var authGroup = app.MapGroup("/authentication");
        authGroup.MapGet(
            "/login",
            (string? returnUrl) =>
                Results.Challenge(
                    new AuthenticationProperties
                    {
                        RedirectUri = IsLocalUrl(returnUrl) ? returnUrl : "/",
                    },
                    [OpenIdConnectDefaults.AuthenticationScheme]
                )
        );

        // GET is needed because Blazor Server's NavigationManager.NavigateTo(forceLoad: true) - the
        // only way to trigger a real HTTP request (and thus an auth-cookie-clearing response) from an
        // interactive circuit - always issues a GET.
        authGroup.MapMethods(
            "/logout",
            ["GET", "POST"],
            (string? returnUrl) =>
                Results.SignOut(
                    new AuthenticationProperties
                    {
                        RedirectUri = IsLocalUrl(returnUrl) ? returnUrl : "/logged-out",
                    },
                    [
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        OpenIdConnectDefaults.AuthenticationScheme,
                    ]
                )
        );
        return app;
    }

    // AuthenticationProperties.RedirectUri (unlike Results.LocalRedirect) isn't restricted to
    // local paths by the auth handlers that consume it, so /authentication/login and /logout must
    // validate returnUrl themselves before using it - otherwise a signed-in user could be bounced to
    // an attacker-controlled URL right after completing a real OIDC login. Mirrors the classic
    // ASP.NET Identity IsLocalUrl check: a bare "/path", not a protocol-relative "//host" or "/\host".
    private static bool IsLocalUrl([NotNullWhen(true)] string? url) =>
        !string.IsNullOrEmpty(url)
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
