using RecruIT.List.Options;
using RecruIT.List.Services;
using RecruIT.List.Services.Access;
using RecruIT.List.Services.Fhir;
using RecruIT.List.Services.Navigation;
using RecruIT.List.Services.Notify;

namespace RecruIT.List.Hosting;

internal static class ServiceExtensions
{
    public static IServiceCollection AddAppOptions(
        this IServiceCollection services,
        IConfiguration configuration
    ) =>
        services
            .Configure<FhirServerOptions>(configuration.GetSection(FhirServerOptions.SectionName))
            .Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName))
            .Configure<NotificationOptions>(
                configuration.GetSection(NotificationOptions.SectionName)
            )
            .Configure<EmailNotificationOptions>(
                configuration.GetSection(EmailNotificationOptions.SectionName)
            )
            .Configure<NotificationTestingOptions>(
                configuration.GetSection(NotificationTestingOptions.SectionName)
            )
            .Configure<TracingOptions>(configuration.GetSection(TracingOptions.SectionName));

    public static IServiceCollection AddAppServices(
        this IServiceCollection services,
        string fhirBaseUrl
    )
    {
        // TODO: the FHIR server call currently carries no credentials at all - same as when Auth:Enabled
        // is false. This app used to forward the signed-in user's OIDC access token (AddUserAccessTokenHttpClient),
        // but per-user FHIR authorization isn't needed right now; a static/service-account credential
        // (the app calling FHIR as itself, not as any particular user) should replace this later.
        services.AddHttpClient(
            FhirClientFactory.HttpClientName,
            client =>
            {
                client.BaseAddress = new Uri(fhirBaseUrl);
                client.DefaultRequestHeaders.Add("Prefer", "handling=strict");
            }
        );

        services.AddScoped<TrialAccessService>();
        services.AddSingleton<FhirClientFactory>();
        services.AddSingleton<RecruitMetrics>();
        services.AddScoped<ScreeningListService>();
        services.AddScoped<TrialListResolver>();
        services.AddScoped<ResearchSubjectService>();
        services.AddScoped<ScreeningNoteService>();
        services.AddScoped<PatientRecordService>();
        services.AddScoped<EligibilityCriteriaService>();
        services.AddScoped<EligibilityFunnelService>();
        services.AddScoped<BreadcrumbState>();
        services.AddScoped<ErrorReporter>();

        services.AddScoped<INotificationChannel, EmailNotificationChannel>();
        services.AddScoped<NotificationSubscriptionService>();
        services.AddScoped<NotificationInboxState>();
        services.AddScoped<NotificationDetectorService>();
        services.AddScoped<NotificationSenderService>();
        services.AddScoped<NotificationTestService>();
        return services;
    }
}
