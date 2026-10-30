using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Localization;
using RecruIT.List.Models;
using RecruIT.List.Resources;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Services.Fhir;

/// <summary>
/// Backs the admin-only Criteria Manager page: lets an admin browse existing ResearchStudy and
/// Library resources, and preview and submit the transaction Bundle (see
/// EligibilityCriteriaBundleBuilder) a study's eligibility criteria consist of against the FHIR
/// server.
/// </summary>
public sealed class EligibilityCriteriaService(
    FhirClientFactory clientFactory,
    IStringLocalizer<SharedResources> localizer,
    ILogger<EligibilityCriteriaService> logger
)
{
    /// <summary>
    /// Every ResearchStudy resource returned by GetResearchStudiesAsync, keyed by id - lets
    /// BuildBundle embed the exact, unmodified resource for a study picked via the "use an
    /// existing study" flow without a second (and necessarily synchronous, since preview building
    /// happens from a Razor property getter) round trip to the FHIR server.
    /// </summary>
    private readonly Dictionary<string, ResearchStudy> _researchStudiesById = [];

    /// <summary>GetResearchStudiesAsync's result, fetched at most once per circuit - see its own doc comment.</summary>
    private List<ResearchStudySummaryDto>? _cachedResearchStudySummaries;

    /// <summary>
    /// Lists every Library resource on the FHIR server (the eligibility-criterion catalog), each
    /// with its decoded SQL and the display names of any ResearchStudy(ies) currently referencing
    /// it (via that study's enrolled Group.characteristic) - resolved by fetching all Group and
    /// ResearchStudy resources and joining in memory, same "fetch + aggregate in C#" convention
    /// used elsewhere in this app. Fine at this app's scale; would need real search-parameter
    /// support (chained search) to stay cheap at a much larger resource count.
    /// </summary>
    public async Task<IReadOnlyList<LibrarySummaryDto>> SearchLibrariesAsync(
        CancellationToken ct = default
    )
    {
        List<Resource> libraries;
        List<Resource> groups;
        List<Resource> studies;
        try
        {
            // Independent searches, run concurrently - each gets its own FhirClient rather than
            // sharing one, since BaseFhirClient tracks last-request state (LastResult etc.) as
            // mutable instance fields that aren't safe to touch from concurrent calls.
            var librariesTask = FhirBundleHelpers.GetAllPagesAsync(
                clientFactory.CreateClient(),
                $"Library?type={Uri.EscapeDataString($"{EligibilityCriteriaBundleBuilder.SqlQueryLibraryTypeSystem}|{EligibilityCriteriaBundleBuilder.SqlQueryLibraryTypeCode}")}"
                    + "&_count=100&_sort=-_lastUpdated",
                ct
            );
            var groupsTask = FhirBundleHelpers.GetAllPagesAsync(
                clientFactory.CreateClient(),
                "Group?_count=100",
                ct
            );
            var studiesTask = FhirBundleHelpers.GetAllPagesAsync(
                clientFactory.CreateClient(),
                "ResearchStudy?_count=100",
                ct
            );

            await Task.WhenAll(librariesTask, groupsTask, studiesTask);
            libraries = await librariesTask;
            groups = await groupsTask;
            studies = await studiesTask;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search Library resources");
            throw new FhirAccessException(localizer["App.Errors.LibrariesLoadFailed"], ex);
        }

        var studiesByGroupId = studies
            .OfType<ResearchStudy>()
            .SelectMany(s => s.Enrollment.Select(e => (Study: s, GroupId: e.GetReferencedId())))
            .Where(x => x.GroupId is not null)
            .ToLookup(x => x.GroupId!, x => x.Study);

        var studyNamesByLibraryId = groups
            .OfType<Group>()
            .Where(g => g.Id is not null)
            .SelectMany(g =>
                g.Characteristic.Select(c => (c.Value as ResourceReference).GetReferencedId())
                    .Where(libraryId => libraryId is not null)
                    .SelectMany(libraryId =>
                        studiesByGroupId[g.Id!]
                            .Select(study => (LibraryId: libraryId!, Study: study))
                    )
            )
            .ToLookup(x => x.LibraryId, x => x.Study.GetStudyDisplayName());

        return libraries
            .OfType<Library>()
            .Where(l => l.Id is not null)
            .Select(l => new LibrarySummaryDto(
                l.Id!,
                l.Title,
                l.Name,
                l.Description,
                l.Status?.ToString(),
                l.GetSqlText(),
                l.GetSqlDialects(),
                l.Meta?.LastUpdated,
                [
                    .. studyNamesByLibraryId[l.Id!]
                        .Distinct()
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                ]
            ))
            .OrderBy(l => l.Title ?? l.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Fetches every ResearchStudy resource on the FHIR server, once per circuit, for the "use an
    /// existing study" picker: assumed to comfortably fit in memory (on the order of up to ~1000
    /// resources), which lets the picker search and lazily reveal them purely client-side rather
    /// than depend on server-side search support that isn't universal - e.g. Blaze doesn't support
    /// the ":contains" string-search modifier a server-side title search would otherwise need.
    /// </summary>
    public async Task<IReadOnlyList<ResearchStudySummaryDto>> GetResearchStudiesAsync(
        CancellationToken ct = default
    )
    {
        if (_cachedResearchStudySummaries is { } cached)
        {
            return cached;
        }

        List<Resource> resources;
        try
        {
            // No _sort param: even "_sort=title" isn't universal (Blaze rejects it - "Unknown
            // search-param `title` in sort clause"), and since every resource gets fetched anyway,
            // sorting client-side below is both simpler and actually portable.
            resources = await FhirBundleHelpers.GetAllPagesAsync(
                clientFactory.CreateClient(),
                "ResearchStudy?_count=100",
                ct
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch ResearchStudy resources");
            throw new FhirAccessException(localizer["App.Errors.ResearchStudiesLoadFailed"], ex);
        }

        var studies = resources.OfType<ResearchStudy>().Where(s => s.Id is not null).ToList();
        foreach (var study in studies)
        {
            _researchStudiesById[study.Id!] = study;
        }

        var summaries = studies
            .Select(s => new ResearchStudySummaryDto(
                s.Id!,
                s.Title,
                s.GetStudyAcronym() is { Length: > 0 } acronym ? acronym : null,
                s.Description,
                s.Status?.ToString()
            ))
            .OrderBy(s => s.Title ?? s.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _cachedResearchStudySummaries = summaries;
        return summaries;
    }

    /// <summary>
    /// Loads an existing study's current eligibility criteria (ResearchStudy.enrollment ->
    /// Group.characteristic -> Library, pulled in via the researchstudy-enrollment and
    /// group-characteristic SearchParameters, see fhir/ig/input/fsh/searchparameters.fsh) as drafts
    /// that reuse their Library by reference - so re-submitting the study keeps them, since
    /// BuildExistingStudyEntry replaces the study's enrollment with a Group of only the drafted
    /// criteria. Characteristics from every enrolled Group are kept, in enrollment order.
    /// </summary>
    public async Task<IReadOnlyList<CriterionDraft>> GetStudyCriteriaAsync(
        string studyId,
        CancellationToken ct = default
    )
    {
        List<Resource> resources;
        try
        {
            resources = await FhirBundleHelpers.GetAllPagesAsync(
                clientFactory.CreateClient(),
                $"ResearchStudy?_id={Uri.EscapeDataString(studyId)}"
                    + "&_include=ResearchStudy:enrollment"
                    + "&_include:iterate=Group:characteristic-reference",
                ct
            );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to load eligibility criteria of ResearchStudy {StudyId}",
                studyId
            );
            throw new FhirAccessException(localizer["App.Errors.StudyCriteriaLoadFailed"], ex);
        }

        var study = resources.OfType<ResearchStudy>().FirstOrDefault(s => s.Id == studyId);
        var groupsById = resources
            .OfType<Group>()
            .Where(g => g.Id is not null)
            .ToDictionary(g => g.Id!);
        var librariesById = resources
            .OfType<Library>()
            .Where(l => l.Id is not null)
            .ToDictionary(l => l.Id!);

        var criteria = new List<CriterionDraft>();
        foreach (var enrollment in study?.Enrollment ?? [])
        {
            if (
                enrollment.GetReferencedId() is not { } groupId
                || !groupsById.TryGetValue(groupId, out var group)
            )
            {
                continue;
            }

            foreach (var characteristic in group.Characteristic)
            {
                if (
                    characteristic.Value is not ResourceReference libraryReference
                    || libraryReference.GetReferencedId() is not { } libraryId
                )
                {
                    logger.LogWarning(
                        "Skipping a characteristic of Group {GroupId} that doesn't reference a Library",
                        groupId
                    );
                    continue;
                }

                // Code.Text first, since that's what gets written back to the characteristic on
                // submit - the Library's own title only as a fallback for characteristics
                // authored without one.
                var library = librariesById.GetValueOrDefault(libraryId);
                criteria.Add(
                    new CriterionDraft
                    {
                        DisplayText =
                            characteristic.Code?.Text
                            ?? library?.Title
                            ?? library?.Name
                            ?? libraryId,
                        Exclude = characteristic.Exclude ?? false,
                        ExistingLibraryReference = $"Library/{libraryId}",
                        Sql = library?.GetSqlText() ?? string.Empty,
                    }
                );
            }
        }

        return criteria;
    }

    /// <summary>The study's criteria Bundle (see EligibilityCriteriaBundleBuilder), serialized to pretty-printed JSON.</summary>
    public string BuildPreviewJson(
        ResearchStudyDraft studyDraft,
        IReadOnlyList<CriterionDraft> criteria
    ) =>
        new FhirJsonSerializer().SerializeToString(BuildBundle(studyDraft, criteria), pretty: true);

    /// <summary>
    /// Submits the same Bundle BuildPreviewJson shows as a transaction against the FHIR server,
    /// upserting every resource in it at once.
    /// </summary>
    public async Task SubmitAsync(
        ResearchStudyDraft studyDraft,
        IReadOnlyList<CriterionDraft> criteria,
        CancellationToken ct = default
    )
    {
        var bundle = BuildBundle(studyDraft, criteria);
        var client = clientFactory.CreateClient();

        try
        {
            await client.TransactionAsync(bundle, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit eligibility criteria transaction bundle");
            throw new FhirAccessException(localizer["App.Errors.CriteriaSubmitFailed"], ex);
        }
    }

    private Bundle BuildBundle(
        ResearchStudyDraft studyDraft,
        IReadOnlyList<CriterionDraft> criteria
    )
    {
        ResearchStudy? existingStudy = null;
        if (
            studyDraft.ExistingStudyId is { } existingStudyId
            && !_researchStudiesById.TryGetValue(existingStudyId, out existingStudy)
        )
        {
            throw new InvalidOperationException(
                $"ResearchStudy/{existingStudyId} is not among the last-searched studies."
            );
        }

        return EligibilityCriteriaBundleBuilder.Build(studyDraft, criteria, existingStudy);
    }
}
