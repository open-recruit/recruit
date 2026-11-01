using RecruIT.List.Components;
using RecruIT.List.Hosting;
using RecruIT.List.Options;
using RecruIT.List.Services.Access;

var builder = WebApplication.CreateBuilder(args);

if (builder.Configuration.IsMigrationOnly())
{
    await builder.RunMigrationsOnlyAsync();
    return;
}

var metricsPort = builder.ConfigureListeners();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAppLocalization();
builder.Services.AddAppOptions(builder.Configuration);

var fhirBaseUrl =
    builder.Configuration.GetValue<string>("FhirServer:BaseUrl")
    ?? throw new InvalidOperationException("FhirServer:BaseUrl must be configured.");
var authEnabled = builder.Configuration.GetValue<bool>("Auth:Enabled");

// Built once from the settings (including the deprecated single-role one, see AuthOptions) and
// injected wherever admin is checked, so they can't disagree.
var adminRoles = new AdminRoles(AuthOptions.ReadAdminRoles(builder.Configuration));
builder.Services.AddSingleton(adminRoles);

var appDbConnectionString = builder.Configuration.GetAppDbConnectionString();
builder.AddObservability(appDbConnectionString);
builder.Services.AddPersistence(appDbConnectionString);
builder.AddAuthentication(authEnabled, adminRoles);
builder.Services.AddAppServices(fhirBaseUrl);

var app = builder.Build();

app.WarnAboutDeprecatedSettings();
app.UseProxyForwardedHeaders();
await app.RunStartupTasksAsync();

// The order below is the request pipeline's order.
app.RestrictMetricsToPort(metricsPort);
app.UseRequestLocalization();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseHangfireDashboardForAdmins(authEnabled);

app.MapHealthEndpoints();
app.MapPrometheusScrapingEndpoint();

if (authEnabled)
{
    app.MapAuthenticationEndpoints();
}

app.MapCultureEndpoint();
app.MapDigestLinkEndpoint();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();

// Exposes the top-level-statement-generated Program class to RecruIT.List.E2ETests, which needs
// it as WebApplicationFactory<Program>'s type parameter.
public partial class Program;
