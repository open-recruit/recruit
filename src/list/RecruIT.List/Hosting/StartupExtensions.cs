using Hangfire;
using RecruIT.List.Options;
using RecruIT.List.Services;
using RecruIT.List.Services.Notify;

namespace RecruIT.List.Hosting;

/// <summary>What runs once at startup, between building the app and serving requests.</summary>
internal static class StartupExtensions
{
    /// <summary>
    /// Lets the dedicated migration Job (charts/recruit/templates/list/migrate-job.yaml) reuse this
    /// exact image/entrypoint to run migrations and exit, without requiring FhirServer__BaseUrl/Auth__Oidc/etc
    /// - those are read unconditionally by the full startup and would otherwise force the Job to carry
    /// irrelevant app config just to avoid an unrelated startup throw. list's own Deployment pods
    /// never set this; they wait for that Job via a wait-for-migration initContainer instead.
    /// </summary>
    public static bool IsMigrationOnly(this IConfiguration configuration) =>
        configuration.GetValue<bool>("RunMigrationsOnly");

    public static async Task RunMigrationsOnlyAsync(this WebApplicationBuilder builder)
    {
        builder.Services.AddDatabase(builder.Configuration.GetAppDbConnectionString());
        await builder.Build().Services.MigrateDatabasesAsync();
    }

    /// <summary>
    /// Logged only after Build() since there's no logger before it - AuthOptions.ReadAdminRoles has
    /// already applied the deprecated setting as the first of the admin roles.
    /// </summary>
    public static void WarnAboutDeprecatedSettings(this WebApplication app)
    {
        if (
            app.Configuration[AuthOptions.DeprecatedAdminRoleKey] is
            { Length: > 0 } deprecatedAdminRole
        )
        {
            app.Logger.LogWarning(
                "Auth:AdminRole is deprecated and will be removed in a future release. It's treated as the first "
                    + "entry of Auth:AdminRoles for now - move it there, e.g. Auth__AdminRoles__0={AdminRole}.",
                deprecatedAdminRole
            );
        }
    }

    public static async Task RunStartupTasksAsync(this WebApplication app)
    {
        await app.Services.MigrateDatabasesAsync();

        // Created up front rather than on first use, so its gauges are exported from the first scrape on
        // instead of only once something has recorded a measurement.
        app.Services.GetRequiredService<RecruitMetrics>();

        // Every list replica runs a Hangfire server (AddHangfireServer); they self-coordinate
        // via the shared Postgres storage, so registering the recurring jobs on every replica's startup is
        // safe and idempotent (Hangfire dedupes by job id). Uses the DI-based IRecurringJobManager rather
        // than the static RecurringJob API, since the static API relies on JobStorage.Current having
        // already been initialized by AddHangfire's lazy configuration - which hasn't necessarily happened
        // yet this early.
        await using var scope = app.Services.CreateAsyncScope();
        NotificationJobs.Register(
            scope.ServiceProvider.GetRequiredService<IRecurringJobManager>(),
            scope
                .ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>()
                .Value.ScanSchedule
        );
    }
}
