using Hl7.Fhir.Model;
using FhirList = Hl7.Fhir.Model.List;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Services.Fhir;

/// <summary>
/// Finds the screening List of a trial known only by its business identifier - what the
/// notification subsystem has, since it persists that and not the List id (see NotificationEvent).
/// Deliberately not cached anywhere: the FHIR List's logical id is server-local and can change
/// (e.g. if a study's screening list is ever recreated), unlike the trial identifier itself. Not
/// access-checked: callers must already know the caller is entitled to see the trial.
/// </summary>
public sealed class TrialListResolver(
    FhirClientFactory clientFactory,
    ILogger<TrialListResolver> logger
)
{
    /// <summary>
    /// The current FHIR List id (and acronym) for the trial with this business identifier token
    /// ("system|value" - see TrialIdentifier), for building a link back into this app; null if the
    /// trial or its List can't be found.
    /// </summary>
    public async Task<(string ListId, string StudyAcronym)?> ResolveListForTrialAsync(
        string trialIdentifierToken,
        CancellationToken ct = default
    )
    {
        var client = clientFactory.CreateClient();

        try
        {
            var studyResources = await FhirBundleHelpers.GetAllPagesAsync(
                client,
                $"ResearchStudy?identifier={Uri.EscapeDataString(trialIdentifierToken)}",
                ct
            );
            var study = studyResources.OfType<ResearchStudy>().FirstOrDefault();
            if (study?.Id is null)
            {
                return null;
            }

            var listResources = await FhirBundleHelpers.GetAllPagesAsync(
                client,
                $"List?belongs-to-study=ResearchStudy/{study.Id}",
                ct
            );
            var list = listResources.OfType<FhirList>().FirstOrDefault();
            if (list?.Id is null)
            {
                return null;
            }

            return (list.Id, study.GetStudyAcronym() ?? list.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Callers decide what a failure means for them (the feed page, the email digest) - this
            // only makes sure it's logged, like every other FHIR read.
            logger.LogError(
                ex,
                "Failed to resolve the screening list of trial {Trial}",
                trialIdentifierToken
            );
            throw;
        }
    }

    /// <summary>
    /// ResolveListForTrialAsync for each distinct token in tokens, at most once per token - the
    /// "memoize across a batch of items that commonly repeat the same handful of trials" pattern
    /// shared by the notification feed page and NotificationSenderService's digest loop.
    /// </summary>
    public async Task<
        IReadOnlyDictionary<string, (string ListId, string StudyAcronym)?>
    > ResolveListsForTrialsAsync(IEnumerable<string> tokens, CancellationToken ct = default)
    {
        var distinctTokens = tokens.Distinct().ToList();
        // Each ResolveListForTrialAsync call creates its own FhirClient, so these are safe to run
        // concurrently - no shared client instance to race on.
        var resolved = await Task.WhenAll(
            distinctTokens.Select(token => ResolveListForTrialAsync(token, ct))
        );
        return distinctTokens.Zip(resolved).ToDictionary(x => x.First, x => x.Second);
    }
}
