using Microsoft.AspNetCore.HttpOverrides;

namespace RecruIT.List.Hosting;

internal static class KestrelExtensions
{
    /// <returns>The port /metrics is served on, apart from the app's own.</returns>
    public static int ConfigureListeners(this WebApplicationBuilder builder)
    {
        // A dedicated metrics port (separate from the app's HTTP_PORT) keeps /metrics off the
        // internet-facing listener - only the in-cluster ServiceMonitor needs to reach it. Configuring
        // both endpoints in code (rather than relying on ASPNETCORE_HTTP_PORTS/ASPNETCORE_URLS) is
        // required here: once Kestrel has any code- or config-based endpoint, it stops honoring the
        // URLs-based env vars for the rest, so both ports must be declared together.
        var httpPort = builder.Configuration.GetValue<int?>("HTTP_PORT") ?? 8080;
        var metricsPort = builder.Configuration.GetValue<int?>("METRICS_PORT") ?? 8081;
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(httpPort);
            kestrel.ListenAnyIP(metricsPort);
        });
        return metricsPort;
    }

    public static IApplicationBuilder UseProxyForwardedHeaders(this IApplicationBuilder app)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders =
                ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost,
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        return app.UseForwardedHeaders(options);
    }
}
