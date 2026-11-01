using System.Security.Claims;

namespace RecruIT.List.Services.Access;

/// <summary>
/// The OIDC roles that make a user a global admin (Auth:AdminRoles) - holding any one of them is
/// enough. Built once at startup and injected wherever admin is checked, so the authorization
/// policy, the dev sign-in bypass and the services all agree on one source.
/// </summary>
public sealed class AdminRoles
{
    public const string DefaultRole = "admin";

    /// <summary>The roles to fall back to when none are configured.</summary>
    public static AdminRoles Default { get; } = new();

    /// <param name="roles">The configured roles; the default role if there are none.</param>
    public AdminRoles(IReadOnlyList<string>? roles = null) =>
        Roles = roles is { Count: > 0 } ? roles : [DefaultRole];

    public IReadOnlyList<string> Roles { get; }

    public bool IsAdmin(ClaimsPrincipal user) => Roles.Any(user.IsInRole);
}
