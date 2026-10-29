using System.Globalization;
using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using RecruIT.List.Options;
using RecruIT.List.Services.Fhir;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Tests;

/// <summary>
/// Shared by every test class that talks to a FHIR server, so they run against one HAPI instance
/// (which takes a while to start) rather than each starting their own. Tests in a collection run
/// one after another, but the server keeps every test's resources - so each test writes under ids
/// of its own (see FhirTestData) and only asserts on those.
/// </summary>
[CollectionDefinition(Name)]
public sealed class FhirServerCollection : ICollectionFixture<FhirServerFixture>
{
    public const string Name = "fhir-server";
}

/// <summary>
/// A disposable HAPI FHIR server with the same custom SearchParameters a real deployment loads -
/// the services' queries depend on them (e.g. List:belongs-to-study), and their search semantics
/// are what these tests are about, which a hand-rolled fake server couldn't faithfully reproduce.
/// </summary>
public sealed class FhirServerFixture : IAsyncLifetime
{
    private const int Port = 8080;

    // Same image as the E2E tests and src/hack/compose.yaml.
    private readonly IContainer _hapi = new ContainerBuilder(
        "docker.io/hapiproject/hapi:v8.12.0-2@sha256:0ff86aac4c7bd13a1ee226444a70263bb46ed1c28ef543c30a44f865b80ca04e"
    )
        .WithPortBinding(Port, assignRandomHostPort: true)
        // Otherwise HAPI hands back an identical search's results for a minute, so a resource
        // written after a search stays invisible to that same search.
        .WithEnvironment("HAPI_FHIR_REUSE_CACHED_SEARCH_RESULTS_MILLIS", "0")
        .WithWaitStrategy(
            Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request =>
                    request.ForPort(Port).ForPath("/fhir/metadata")
                )
        )
        .Build();

    public string BaseUrl { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        await _hapi.StartAsync();
        BaseUrl =
            $"http://{_hapi.Hostname}:{_hapi.GetMappedPublicPort(Port).ToString(CultureInfo.InvariantCulture)}/fhir";

        // SearchParameters first: resources are only indexed for the ones that already exist when
        // they're written.
        var searchParameters = new FhirJsonDeserializer().Deserialize<Bundle>(
            await File.ReadAllTextAsync(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "fhir",
                    "search-parameters-transaction-upsert.json"
                )
            )
        );
        await CreateClient().TransactionAsync(searchParameters);
        await WaitUntilSearchableAsync(searchParameters, TimeSpan.FromSeconds(60));
    }

    public async ValueTask DisposeAsync() => await _hapi.DisposeAsync();

    /// <summary>What the services under test get - the app's own factory, pointed at this server.</summary>
    /// <param name="handler">
    /// Sits between the service and the server, e.g. to make something happen in between two of a
    /// service's requests. Created anew for every client.
    /// </param>
    public FhirClientFactory CreateClientFactory(Func<DelegatingHandler>? handler = null) =>
        new(
            new PlainHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new FhirServerOptions { BaseUrl = BaseUrl })
        );

    /// <summary>For tests' own setup and assertions.</summary>
    public FhirClient CreateClient() => CreateClientFactory().CreateClient();

    /// <summary>
    /// HAPI picks up new SearchParameters with a short delay, and rejects searches on one it
    /// doesn't know yet with a 400 - so this waits until each of them is searchable.
    /// </summary>
    private async Task WaitUntilSearchableAsync(Bundle searchParameters, TimeSpan timeout)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/") };
        var deadline = DateTime.UtcNow + timeout;
        var probes = searchParameters
            .Entry.Select(e => e.Resource)
            .OfType<SearchParameter>()
            .SelectMany(p =>
                p.Base.Select(type => $"{type}?{p.Code}=probe-{Guid.NewGuid():N}&_summary=count")
            );

        foreach (var probe in probes)
        {
            while (true)
            {
                using var response = await http.GetAsync(probe);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }

                if (response.StatusCode != HttpStatusCode.BadRequest || DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"'{probe}' wasn't searchable within {timeout} (last status {response.StatusCode})."
                    );
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    private sealed class PlainHttpClientFactory(Func<DelegatingHandler>? handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            if (handler is null)
            {
                return new HttpClient();
            }

            var delegating = handler();
            delegating.InnerHandler = new HttpClientHandler();
            return new HttpClient(delegating);
        }
    }
}
