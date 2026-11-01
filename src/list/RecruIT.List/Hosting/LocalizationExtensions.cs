using BlazorBlueprint.Components;
using RecruIT.List.Services.Localization;

namespace RecruIT.List.Hosting;

internal static class LocalizationExtensions
{
    public static readonly string[] SupportedCultures = ["en", "de"];

    public static IServiceCollection AddAppLocalization(this IServiceCollection services)
    {
        services.AddLocalization();
        services.AddBlazorBlueprintComponents();
        services.AddScoped<IBbLocalizer, AppBbLocalizer>();

        services.Configure<RequestLocalizationOptions>(options =>
        {
            options
                .SetDefaultCulture(SupportedCultures[0])
                .AddSupportedCultures(SupportedCultures)
                .AddSupportedUICultures(SupportedCultures);
        });
        return services;
    }
}
