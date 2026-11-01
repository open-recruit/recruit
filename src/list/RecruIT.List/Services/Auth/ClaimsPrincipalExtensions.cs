using System.Security.Claims;

namespace RecruIT.List.Services.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Set by this app at sign-in (see OidcEvents.MarkEmailTrust, DevBypassAuthenticationHandler)
    /// to "true" when the user's email address can be treated as theirs. Deliberately not the IdP's
    /// own "email_verified" claim: whether to rely on that is a deployment decision
    /// (Auth:RequireVerifiedEmail), and a claim only this app writes can't be spoofed by a token.
    /// </summary>
    public const string EmailTrustedClaimType = "recruit_email_trusted";

    /// <summary>
    /// The user's full display name (OIDC "name" claim). Falls back to whatever NameClaimType
    /// resolves to - "preferred_username" per Program.cs's OIDC config - only if "name" is unset,
    /// so UI surfaces show a real name ("Jane Doe") rather than a login handle ("jdoe") wherever possible.
    /// </summary>
    public static string? GetDisplayName(this ClaimsPrincipal user) =>
        user.FindFirst("name")?.Value
        ?? user.FindFirst(ClaimTypes.Name)?.Value
        ?? user.Identity?.Name;

    /// <summary>OIDC "sub" claim, falling back to the standard NameIdentifier claim type.</summary>
    public static string? GetSubjectId(this ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    /// <summary>
    /// OIDC "email" claim, falling back to the standard Email claim type - as the IdP reported it,
    /// fine for display but not for deciding what the user may access (see GetVerifiedEmail).
    /// </summary>
    public static string? GetEmail(this ClaimsPrincipal user) =>
        user.FindFirst("email")?.Value ?? user.FindFirst(ClaimTypes.Email)?.Value;

    /// <summary>
    /// The email address, only if the sign-in established it as the user's own (see
    /// EmailTrustedClaimType) - anyone able to register at the IdP with someone else's address
    /// would otherwise inherit that person's grants.
    /// </summary>
    public static string? GetVerifiedEmail(this ClaimsPrincipal user) =>
        user.HasClaim(EmailTrustedClaimType, "true") ? user.GetEmail() : null;

    /// <summary>
    /// The two identities a user's TrialAccessGrant/NotificationSubscription rows are looked up by
    /// - sub is primary, email is the fallback for grants created before the user's first login
    /// (see OidcEvents, which backfills SubjectId once they do log in). Only a trusted email
    /// counts, so a user whose address the IdP hasn't verified has just their sub.
    /// </summary>
    public static (string? SubjectId, string? Email) GetUserKeys(this ClaimsPrincipal user) =>
        (user.GetSubjectId(), user.GetVerifiedEmail());
}
