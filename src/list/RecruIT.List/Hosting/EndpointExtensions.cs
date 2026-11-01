using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Localization;
using RecruIT.List.Services.Fhir;
using RecruIT.List.Services.Notify;

namespace RecruIT.List.Hosting;

/// <summary>The minimal-API endpoints that sit next to the Razor components.</summary>
internal static class EndpointExtensions
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/livez", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks(
            "/readyz",
            new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }
        );
        return app;
    }

    public static IEndpointRouteBuilder MapCultureEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/culture/set",
            (string culture, string? redirectUri, HttpContext ctx) =>
            {
                var resolvedCulture = LocalizationExtensions.SupportedCultures.Contains(culture)
                    ? culture
                    : LocalizationExtensions.SupportedCultures[0];

                ctx.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(
                        new RequestCulture(resolvedCulture)
                    ),
                    new CookieOptions
                    {
                        Expires = DateTimeOffset.UtcNow.AddYears(1),
                        IsEssential = true,
                        HttpOnly = true,
                        // Not hardcoded true: this app also runs over plain HTTP in local dev and in the
                        // Playwright E2E tests, where a hardcoded Secure flag would make the browser
                        // silently drop the cookie. Matches the SameAsRequest default already used by the
                        // OIDC cookie.
                        Secure = ctx.Request.IsHttps,
                    }
                );

                return Results.LocalRedirect(string.IsNullOrEmpty(redirectUri) ? "/" : redirectUri);
            }
        );
        return app;
    }

    /// <summary>
    /// Every notification digest email links here (see NotificationSenderService.EmailDigestLink):
    /// marks the in-app items that email covered read, then forwards to the trial it was about.
    /// Requires sign-in - challenging via OIDC and coming back here if needed - rather than trusting
    /// the link alone, so neither a forwarded email nor a mail scanner prefetching links can mark
    /// anything read on the subscriber's behalf. The target List is resolved now rather than baked
    /// into the email at send time, since a trial's List id isn't guaranteed stable (see
    /// NotificationEvent).
    /// </summary>
    public static IEndpointRouteBuilder MapDigestLinkEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/notifications/email/{digestId:guid}",
                async (
                    Guid digestId,
                    ClaimsPrincipal user,
                    NotificationSubscriptionService notifications,
                    TrialListResolver trialLists,
                    CancellationToken ct
                ) =>
                {
                    var trialToken = await notifications.MarkEmailDigestReadAsync(
                        digestId,
                        user,
                        ct
                    );
                    if (
                        trialToken is not null
                        && await trialLists.ResolveListForTrialAsync(trialToken, ct) is { } listInfo
                    )
                    {
                        return Results.LocalRedirect(
                            $"/trials/{Uri.EscapeDataString(listInfo.ListId)}/patients"
                        );
                    }

                    // Unknown, someone else's, or a test email's digest - or a trial whose List can't be
                    // resolved right now.
                    return Results.LocalRedirect("/notifications");
                }
            )
            .RequireAuthorization();
        return app;
    }
}
