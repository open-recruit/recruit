using System.Security.Claims;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Localization;
using RecruIT.List.Models;
using RecruIT.List.Resources;
using RecruIT.List.Services.Access;
using FhirList = Hl7.Fhir.Model.List;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Services.Fhir;

public sealed class ScreeningListService(
    FhirClientFactory clientFactory,
    TrialAccessService accessService,
    IStringLocalizer<SharedResources> localizer,
    ILogger<ScreeningListService> logger
)
{
    public async Task<IReadOnlyList<TrialSummaryDto>> GetAccessibleTrialsAsync(
        ClaimsPrincipal user,
        CancellationToken ct = default
    )
    {
        var client = clientFactory.CreateClient();

        var query = ScreeningListMapping.ScreeningListsQuery("current,retired");

        List<Resource> resources;
        try
        {
            resources = await FhirBundleHelpers.GetAllPagesAsync(client, query, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch screening lists from the FHIR server");
            throw new FhirAccessException(localizer["App.Errors.TrialsLoadFailed"], ex);
        }

        var researchSubjectsById = resources.ToLookupById<ResearchSubject>();
        var lists = resources.OfType<FhirList>().ToList();

        var trialInfoByListId = ScreeningListMapping.BuildTrialInfoByListId(resources, lists);
        var accessibleTrials = await accessService.GetAccessibleTrialIdentifiersAsync(user, ct);

        var summaries = new List<TrialSummaryDto>();
        foreach (
            var (list, trialInfo) in ScreeningListMapping.GetAccessibleLists(
                lists,
                trialInfoByListId,
                accessibleTrials
            )
        )
        {
            var (recruited, pending, notRecruited) = ScreeningListMapping.CountByStatus(
                (list.Entry ?? []).Select(entry =>
                {
                    var subjectId = entry.Item.GetReferencedId();
                    var subject =
                        subjectId is not null
                        && researchSubjectsById.TryGetValue(subjectId, out var s)
                            ? s
                            : null;
                    return subject is not null ? EnumUtility.GetLiteral(subject.Status) : null;
                })
            );

            summaries.Add(
                new TrialSummaryDto
                {
                    ListId = list.Id!,
                    StudyId = trialInfo.StudyId,
                    TrialIdentifier = trialInfo.Identifier,
                    StudyAcronym = trialInfo.Acronym!,
                    StudyTitle = trialInfo.Title,
                    ListStatus =
                        EnumUtility.GetLiteral(list.Status) ?? FhirVocabulary.ListStatusCurrent,
                    RecruitedCount = recruited,
                    PendingCount = pending,
                    NotRecruitedCount = notRecruited,
                }
            );
        }

        return summaries.OrderBy(s => s.StudyAcronym, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Patient-centric view of the same data the dashboard uses: instead of one row per trial, this
    /// groups every accessible List's entries by patient, so a patient recommended for several
    /// trials shows up once with a membership entry per trial ("potentially fitting studies").
    /// </summary>
    public async Task<IReadOnlyList<PatientOverviewDto>> GetPatientsAcrossTrialsAsync(
        ClaimsPrincipal user,
        CancellationToken ct = default
    )
    {
        var client = clientFactory.CreateClient();

        var query = ScreeningListMapping.ScreeningListsQuery("current");

        List<Resource> resources;
        try
        {
            resources = await FhirBundleHelpers.GetAllPagesAsync(client, query, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch screening lists for the patient overview");
            throw new FhirAccessException(localizer["App.Errors.PatientOverviewLoadFailed"], ex);
        }

        var subjectsById = resources.ToLookupById<ResearchSubject>();
        var patientsById = resources.ToLookupById<Patient>();
        var lists = resources.OfType<FhirList>().ToList();

        var trialInfoByListId = ScreeningListMapping.BuildTrialInfoByListId(resources, lists);
        var accessibleTrials = await accessService.GetAccessibleTrialIdentifiersAsync(user, ct);

        var overviewsByPatient = new Dictionary<string, PatientOverviewDto>();

        foreach (
            var (list, trialInfo) in ScreeningListMapping.GetAccessibleLists(
                lists,
                trialInfoByListId,
                accessibleTrials
            )
        )
        {
            foreach (var entry in list.Entry ?? [])
            {
                var subjectId = entry.Item.GetReferencedId();
                if (subjectId is null || !subjectsById.TryGetValue(subjectId, out var subject))
                {
                    continue;
                }

                var patientId = subject.Individual.GetReferencedId();
                if (patientId is null)
                {
                    continue;
                }

                var patient = patientsById.TryGetValue(patientId, out var p) ? p : null;

                if (!overviewsByPatient.TryGetValue(patientId, out var overview))
                {
                    overview = new PatientOverviewDto
                    {
                        PatientId = patientId,
                        DisplayName = FhirBundleHelpers.GetPatientDisplayName(patient, patientId),
                        BirthDate = FhirBundleHelpers.ParseFhirInstant(patient?.BirthDate),
                        Gender = EnumUtility.GetLiteral(patient?.Gender),
                        Trials = [],
                    };
                    overviewsByPatient[patientId] = overview;
                }

                overview.Trials.Add(
                    ScreeningListMapping.ToEntry(
                        subject,
                        patient,
                        patientId,
                        entry,
                        (list, trialInfo)
                    )
                );
            }
        }

        foreach (var overview in overviewsByPatient.Values)
        {
            overview.Trials.Sort(
                (a, b) =>
                    string.Compare(
                        a.StudyAcronym,
                        b.StudyAcronym,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
        }

        return overviewsByPatient
            .Values.OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<(
        TrialSummaryDto Summary,
        IReadOnlyList<PatientListEntryDto> Patients
    )> GetListWithPatientsAsync(string listId, ClaimsPrincipal user, CancellationToken ct = default)
    {
        var client = clientFactory.CreateClient();

        var query =
            $"List?_id={Uri.EscapeDataString(listId)}"
            + "&_include=List:item"
            + "&_include:iterate=ResearchSubject:patient"
            + "&_include=List:belongs-to-study"
            + "&_include:iterate=ResearchStudy:enrollment"
            + "&_include:iterate=Group:characteristic-reference";

        List<Resource> resources;
        try
        {
            resources = await FhirBundleHelpers.GetAllPagesAsync(client, query, ct);
        }
        catch (Exception ex)
        {
            // listId comes straight from the route - strip line breaks so it can't forge log entries.
            logger.LogError(
                ex,
                "Failed to fetch List/{ListId} from the FHIR server",
                listId.ReplaceLineEndings(string.Empty)
            );
            throw new FhirAccessException(localizer["App.Errors.PatientListLoadFailed"], ex);
        }

        var list =
            resources.OfType<FhirList>().FirstOrDefault()
            ?? throw new FhirAccessException(localizer["App.Errors.ListNotFound"]);

        // The acronym, trial identifier (see TrialIdentifier) and title/description all live on
        // the ResearchStudy itself - pulled in via the belongs-to-study SearchParameter (see
        // fhir/ig/input/fsh/searchparameters.fsh) rather than trusting a Reference.display
        // text cached on the List extension (see GetStudyAcronym).
        var study = resources.OfType<ResearchStudy>().FirstOrDefault();
        var acronym = study?.GetStudyAcronym();
        var trialIdentifier = study?.GetTrialIdentifier();
        if (
            string.IsNullOrEmpty(acronym)
            || trialIdentifier is null
            || !await accessService.CanAccessTrialAsync(user, trialIdentifier, ct)
        )
        {
            throw new UnauthorizedAccessException(localizer["App.Errors.NotAuthorizedTrial"]);
        }

        // ResearchStudy.enrollment -> Group.characteristic, pulled in via the
        // researchstudy-enrollment SearchParameter (see
        // fhir/ig/input/fsh/searchparameters.fsh) rather than a separate fetch.
        var enrolledGroupId = study?.Enrollment?.FirstOrDefault()?.GetReferencedId();
        var group = enrolledGroupId is not null
            ? resources.OfType<Group>().FirstOrDefault(g => g.Id == enrolledGroupId)
            : null;
        var criteria = ScreeningListMapping.ToCriteria(group, resources.ToLookupById<Library>());

        var subjectsById = resources.ToLookupById<ResearchSubject>();
        var patientsById = resources.ToLookupById<Patient>();

        var patientEntries = new List<PatientListEntryDto>();

        foreach (var entry in list.Entry ?? [])
        {
            var subjectId = entry.Item.GetReferencedId();
            if (subjectId is null || !subjectsById.TryGetValue(subjectId, out var subject))
            {
                continue;
            }

            var patientId = subject.Individual.GetReferencedId();
            var patient =
                patientId is not null && patientsById.TryGetValue(patientId, out var p) ? p : null;

            patientEntries.Add(
                ScreeningListMapping.ToEntry(subject, patient, patientId ?? string.Empty, entry)
            );
        }

        var (recruited, pending, notRecruited) = ScreeningListMapping.CountByStatus(
            patientEntries.Select(p => p.Status)
        );

        var summary = new TrialSummaryDto
        {
            ListId = list.Id!,
            // study is guaranteed non-null here: acronym/trialIdentifier above are both derived
            // from it, and the guard above already rejected a null/empty either of those.
            StudyId = study!.Id!,
            TrialIdentifier = trialIdentifier,
            StudyAcronym = acronym,
            StudyTitle = study?.Title,
            StudyDescription = study?.Description,
            Criteria = criteria,
            ListStatus = EnumUtility.GetLiteral(list.Status) ?? FhirVocabulary.ListStatusCurrent,
            RecruitedCount = recruited,
            PendingCount = pending,
            NotRecruitedCount = notRecruited,
        };

        return (summary, patientEntries.OrderByDescending(p => p.RecommendedDate).ToList());
    }

    /// <summary>Retires or reactivates a screening list - admins only.</summary>
    public async Task UpdateListStatusAsync(
        string listId,
        FhirList.ListStatus status,
        ClaimsPrincipal user,
        CancellationToken ct = default
    )
    {
        if (!accessService.CanPatchList(user))
        {
            throw new UnauthorizedAccessException(localizer["App.Errors.AdminOnlyTrialStatus"]);
        }

        var client = clientFactory.CreateClient();
        // Built by a serializer from the typed status, not by splicing text into JSON.
        var patchDocument = JsonSerializer.Serialize(
            new[]
            {
                new
                {
                    op = "replace",
                    path = "/status",
                    value = EnumUtility.GetLiteral(status),
                },
            }
        );

        try
        {
            // PatchAsync<TResource> builds the "{ResourceType}/{id}" path itself from TResource, so
            // the id argument must be bare (no "List/" prefix) or the two collide into an invalid
            // "List/List" path.
            await client.PatchAsync<FhirList>(listId, patchDocument, ResourceFormat.Json, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update status for List/{ListId}", listId);
            throw new FhirAccessException(localizer["App.Errors.TrialStatusUpdateFailed"], ex);
        }
    }
}
