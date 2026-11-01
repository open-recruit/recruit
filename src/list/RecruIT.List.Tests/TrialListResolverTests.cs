using Microsoft.Extensions.Logging;
using RecruIT.List.Models;
using RecruIT.List.Services.Fhir;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Tests;

[Collection(FhirServerCollection.Name)]
public sealed class TrialListResolverTests(FhirServerFixture fhir)
{
    private readonly FhirTestData _data = new(fhir.CreateClient());

    private TrialListResolver CreateResolver(
        Func<DelegatingHandler>? handler = null,
        ILogger<TrialListResolver>? log = null
    ) =>
        new(
            fhir.CreateClientFactory(handler),
            log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TrialListResolver>.Instance
        );

    [Fact]
    public async Task A_trial_identifier_resolves_to_its_list()
    {
        var study = _data.Study("RESOLVE");
        var list = _data.ScreeningList(study, []);
        await _data.PutAsync(study, list);
        var token = FhirTestData.TrialOf(study).ToToken();
        var unknown = new TrialIdentifier(
            FhirTestData.TrialIdentifierSystem,
            _data.Id("unknown")
        ).ToToken();

        var resolved = await CreateResolver()
            .ResolveListsForTrialsAsync(
                [token, unknown, token],
                TestContext.Current.CancellationToken
            );

        Assert.Equal(2, resolved.Count);
        Assert.Equal((list.Id!, "RESOLVE"), resolved[token]);
        Assert.Null(resolved[unknown]);
    }

    [Fact]
    public async Task A_failure_resolving_a_trial_is_logged_and_still_reaches_the_caller()
    {
        var log = new CapturingLogger<TrialListResolver>();
        var token = new TrialIdentifier(
            FhirTestData.TrialIdentifierSystem,
            "unreachable"
        ).ToToken();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateResolver(() => new UnreachableFhirServer(), log)
                .ResolveListForTrialAsync(token, TestContext.Current.CancellationToken)
        );

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.NotNull(entry.Exception);
        Assert.Contains(token, entry.Message);
    }

    /// <summary>A FHIR server that can't be reached.</summary>
    private sealed class UnreachableFhirServer : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new HttpRequestException("connection refused");
    }
}
