using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RecruIT.List.Options;
using RecruIT.List.Services;

namespace RecruIT.List.Hosting;

internal static class ObservabilityExtensions
{
    public static WebApplicationBuilder AddObservability(
        this WebApplicationBuilder builder,
        string appDbConnectionString
    )
    {
        // Liveness (/livez) intentionally checks nothing but that the process can still handle a request -
        // it shouldn't flap just because a downstream dependency is briefly unavailable. Readiness (/readyz)
        // only runs checks tagged "ready", currently just the DB, since that's the one dependency this app
        // can't do anything useful without.
        builder
            .Services.AddHealthChecks()
            .AddNpgSql(appDbConnectionString, name: "postgres", tags: ["ready"]);

        var otelBuilder = builder
            .Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
                metrics
                    .AddAspNetCoreInstrumentation()
                    // Blazor Server's own meters (circuits, navigation, event handling, rendering) - not
                    // part of AddAspNetCoreInstrumentation, which only covers HTTP-level ones.
                    .AddMeter(
                        "Microsoft.AspNetCore.Components",
                        "Microsoft.AspNetCore.Components.Lifecycle",
                        "Microsoft.AspNetCore.Components.Server.Circuits"
                    )
                    .AddHttpClientInstrumentation()
                    .AddNpgsqlInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(RecruitMetrics.MeterName)
                    .AddPrometheusExporter()
            );

        var tracingEnabled = builder.Configuration.GetValue<bool>(
            $"{TracingOptions.SectionName}:Enabled"
        );

        // Opt-in rather than always-on: exporting spans to a collector nobody deployed is just
        // per-request overhead for nothing (metrics above stay unconditional since Prometheus scraping is
        // already always-on and cheap). AddOtlpExporter() with no config reads the standard
        // OTEL_EXPORTER_OTLP_ENDPOINT/_PROTOCOL/OTEL_SERVICE_NAME env vars (or appsettings keys of the
        // same name) itself - the same convention the other OTEL_*-instrumented services in
        // src/hack/compose.yaml already use, so no separate endpoint setting is needed here.
        // AddHttpClientInstrumentation covers every HttpClient in the app by DiagnosticSource, including
        // the named "fhir" client (Services/Fhir/FhirClientFactory) - there's no separate per-client setup.
        if (tracingEnabled)
        {
            otelBuilder.WithTracing(tracing =>
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddOtlpExporter()
            );
        }

        return builder;
    }

    /// <summary>
    /// Keeps /metrics off the app's public-facing port: ASP.NET Core routing doesn't care which Kestrel
    /// listener accepted a connection, so without this, ingress traffic (which forwards "/" to the same
    /// port list's main listener serves) could reach the Prometheus exporter too - defeating the
    /// point of a separate metrics port. Checks the actual accepted connection's local port rather than
    /// RequireHost's Host-header matching, since the Host header can desync from the real port behind
    /// port-forwarding/NAT. /livez and /readyz don't get the same treatment: kubelet always probes the
    /// "http" port by name, and a plain Healthy/Unhealthy response isn't sensitive either way.
    /// </summary>
    public static IApplicationBuilder RestrictMetricsToPort(
        this IApplicationBuilder app,
        int metricsPort
    ) =>
        app.Use(
            async (context, next) =>
            {
                if (
                    context.Request.Path == "/metrics"
                    && context.Connection.LocalPort != metricsPort
                )
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next(context);
            }
        );
}
