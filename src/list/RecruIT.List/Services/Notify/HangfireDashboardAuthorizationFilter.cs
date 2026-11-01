using Hangfire.Dashboard;
using RecruIT.List.Services.Access;

namespace RecruIT.List.Services.Notify;

/// <summary>
/// Gates /hangfire to admins, same role check as the AuthPolicies.Admin authorization policy used elsewhere.
/// Hangfire's dashboard middleware sits outside ASP.NET Core's authorization pipeline, so it needs
/// its own filter rather than an [Authorize] attribute or MapGroup policy.
/// </summary>
public sealed class HangfireDashboardAuthorizationFilter(bool authEnabled, AdminRoles adminRoles)
    : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        if (!authEnabled)
        {
            return true;
        }

        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true
            && adminRoles.IsAdmin(httpContext.User);
    }
}
