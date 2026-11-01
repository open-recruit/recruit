using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RecruIT.List.Data;
using RecruIT.List.Services.Access;
using RecruIT.List.Services.Notify;

namespace RecruIT.List.Hosting;

/// <summary>The Postgres-backed parts of the app: its own data, the Data Protection keys and Hangfire.</summary>
internal static class DatabaseExtensions
{
    public static string GetAppDbConnectionString(this IConfiguration configuration) =>
        new NpgsqlConnectionStringBuilder(
            configuration.GetConnectionString("AppDb")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:AppDb must be configured."
                )
        ).ConnectionString;

    /// <summary>
    /// What the migrations cover - all the migration-only job needs, and the base of
    /// <see cref="AddPersistence"/>.
    /// </summary>
    public static IServiceCollection AddDatabase(
        this IServiceCollection services,
        string connectionString
    )
    {
        // AddDbContextFactory (not AddDbContext) is required in Blazor Server: DI scopes there are
        // per-circuit (roughly per user session), not per-request, so a directly-injected DbContext would
        // live far longer than intended. Services instead resolve IDbContextFactory<AppDbContext> and
        // create a short-lived context per operation.
        services.AddDbContextFactory<AppDbContext>(options =>
            options
                .UseNpgsql(connectionString, npgsql => npgsql.MapAppDbEnums())
                .UseSnakeCaseNamingConvention()
        );
        services.AddDbContext<DataProtectionKeyContext>(options =>
            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention()
        );
        return services;
    }

    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        string connectionString
    )
    {
        services.AddDatabase(connectionString);

        // Blazor Server keeps one process per replica but many circuits/cookies across replicas - the
        // Data Protection key ring that encrypts auth cookies/antiforgery tokens needs to be shared so a
        // request landing on a different replica than the one that issued the cookie can still decrypt it
        // (relevant even with ingress session affinity, e.g. right after a scaling event). This must not
        // be scoped to Authorization:IsEnabled - app.UseAntiforgery() is unconditional (Blazor
        // Server's own circuit handshake relies on it regardless of whether OIDC auth is on), so even an
        // auth-disabled deployment needs a shared key ring across replicas, not just a single-process
        // fallback. Persisted to the same Postgres database as AppDb rather than a separate Redis instance
        // - sticky sessions themselves are an ingress-level concern, documented in the README, not
        // implemented here.
        services.AddDataProtection().PersistKeysToDbContext<DataProtectionKeyContext>();

        // Hangfire coordinates the notification poller across all list replicas via this same
        // Postgres database - see Services/Notify and the notify-next plan for why (a plain
        // UPDATE ... WHERE last_seen_version_id = @original concurrency-token check on PollCursor is the
        // actual dedup mechanism; Hangfire just gives each replica a clustered recurring trigger and a
        // distributed, retried delivery queue on top of that).
        services.AddHangfire(config =>
            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(options =>
                    options.UseNpgsqlConnection(connectionString, _ => { })
                )
        );
        services.AddHangfireServer();
        return services;
    }

    public static IApplicationBuilder UseHangfireDashboardForAdmins(
        this WebApplication app,
        bool authEnabled
    ) =>
        app.UseHangfireDashboard(
            "/hangfire",
            new DashboardOptions
            {
                Authorization =
                [
                    new HangfireDashboardAuthorizationFilter(
                        authEnabled,
                        app.Services.GetRequiredService<AdminRoles>()
                    ),
                ],
            }
        );

    public static async Task MigrateDatabasesAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<
            IDbContextFactory<AppDbContext>
        >();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        var dataProtectionKeyContext =
            scope.ServiceProvider.GetRequiredService<DataProtectionKeyContext>();
        await dataProtectionKeyContext.Database.MigrateAsync();
    }
}
