using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;
using RecruIT.List.Models;
using RecruIT.List.Services.Access;
using FhirList = Hl7.Fhir.Model.List;

namespace RecruIT.List.Services.Fhir;

/// <summary>
/// How the screening lists FHIR returns - Lists with their included ResearchSubjects, Patients and
/// ResearchStudies - become the app's read models (see ScreeningListService): the query, the trial
/// each list belongs to, which of them a user may see, and the DTOs built from them.
/// </summary>
internal static class ScreeningListMapping
{
    /// <summary>The trial a List belongs to, as read off its ResearchStudy.</summary>
    public sealed record TrialInfo(
        TrialIdentifier Identifier,
        string? Acronym,
        string? Title,
        string StudyId
    );

    /// <summary>
    /// The List?code=...&amp;_include=... query shared by every method that scans screening lists
    /// across trials (dashboard, patient overview) - only the status filter varies.
    /// </summary>
    public static string ScreeningListsQuery(string status) =>
        $"List?code={Uri.EscapeDataString($"{FhirVocabulary.SystemScreeningList}|{FhirVocabulary.ScreeningListCode}")}"
        + $"&status={status}"
        + "&_include=List:item"
        + "&_include:iterate=ResearchSubject:patient"
        + "&_include=List:belongs-to-study"
        + "&_count=100";

    /// <summary>
    /// Resolves each List's ResearchStudy - its business identifier (see TrialIdentifier) and its
    /// acronym, read live off the ResearchStudy rather than trusting a Reference.display text
    /// cached on the List - from ResearchStudy resources already pulled into the same response via
    /// the belongs-to-study SearchParameter (see fhir/ig/input/fsh/searchparameters.fsh),
    /// so this needs no request of its own.
    /// </summary>
    public static Dictionary<string, TrialInfo> BuildTrialInfoByListId(
        IReadOnlyList<Resource> resources,
        IEnumerable<FhirList> lists
    )
    {
        var studiesById = resources.ToLookupById<ResearchStudy>();

        var result = new Dictionary<string, TrialInfo>();
        foreach (var list in lists)
        {
            var studyId = list.GetReferenceExtension(FhirVocabulary.UrlListBelongsToStudy)
                ?.GetReferencedId();
            if (
                list.Id is null
                || studyId is null
                || !studiesById.TryGetValue(studyId, out var study)
            )
            {
                continue;
            }

            if (study.GetTrialIdentifier() is { } identifier)
            {
                result[list.Id] = new TrialInfo(
                    identifier,
                    study.GetStudyAcronym(),
                    study.Title,
                    studyId
                );
            }
        }

        return result;
    }

    /// <summary>
    /// Filters lists down to those whose trial the user can access, paired with the TrialInfo
    /// resolved for each - the "resolve, then skip if unauthorized" step every cross-trial scan
    /// needs before it can build its own view-specific DTOs.
    /// </summary>
    public static IEnumerable<(FhirList List, TrialInfo Info)> GetAccessibleLists(
        IEnumerable<FhirList> lists,
        IReadOnlyDictionary<string, TrialInfo> trialInfoByListId,
        IReadOnlySet<TrialIdentifier>? accessibleTrials
    )
    {
        foreach (var list in lists)
        {
            var trialInfo = list.Id is not null
                ? trialInfoByListId.GetValueOrDefault(list.Id)
                : null;
            if (
                trialInfo is null
                || string.IsNullOrEmpty(trialInfo.Acronym)
                || !TrialAccessService.CanAccessTrial(accessibleTrials, trialInfo.Identifier)
            )
            {
                continue;
            }

            yield return (list, trialInfo);
        }
    }

    /// <summary>Buckets ResearchSubject statuses into the three counts every trial/list summary shows.</summary>
    public static (int Recruited, int Pending, int NotRecruited) CountByStatus(
        IEnumerable<string?> statuses
    )
    {
        int recruited = 0,
            pending = 0,
            notRecruited = 0;

        foreach (var status in statuses)
        {
            if (status is not null && FhirVocabulary.RecruitedStatuses.Contains(status))
            {
                recruited++;
            }
            else if (status is not null && FhirVocabulary.NotRecruitedStatuses.Contains(status))
            {
                notRecruited++;
            }
            else
            {
                pending++;
            }
        }

        return (recruited, pending, notRecruited);
    }

    /// <summary>
    /// One List entry as a row: the subject's status and the patient's demographics. In a
    /// cross-trial view (a patient can then appear under several lists) it also carries which list
    /// and trial it came from.
    /// </summary>
    public static PatientListEntryDto ToEntry(
        ResearchSubject subject,
        Patient? patient,
        string patientId,
        FhirList.EntryComponent entry,
        (FhirList List, TrialInfo Info)? crossTrial = null
    ) =>
        new()
        {
            ResearchSubjectId = subject.Id!,
            ResearchSubjectIdentifier = subject.GetResearchSubjectIdentifierToken(),
            PatientId = patientId,
            DisplayName = FhirBundleHelpers.GetPatientDisplayName(patient, patientId),
            MedicalRecordNumber = patient?.GetMedicalRecordNumber(),
            BirthDate = FhirBundleHelpers.ParseFhirInstant(patient?.BirthDate),
            Gender = EnumUtility.GetLiteral(patient?.Gender),
            Phone = TelecomValue(patient, ContactPoint.ContactPointSystem.Phone),
            Email = TelecomValue(patient, ContactPoint.ContactPointSystem.Email),
            Status =
                EnumUtility.GetLiteral(subject.Status) ?? FhirVocabulary.SubjectStatusCandidate,
            RecommendedDate = FhirBundleHelpers.ParseFhirInstant(entry.Date),
            SystemDeterminedIneligible = IsFlaggedIneligible(entry),
            LastUpdated = subject.Meta?.LastUpdated,
            ListId = crossTrial?.List.Id,
            StudyAcronym = crossTrial?.Info.Acronym,
            StudyTitle = crossTrial?.Info.Title,
            TrialIdentifier = crossTrial?.Info.Identifier,
        };

    /// <summary>
    /// A study's eligibility criteria: its enrolled Group's characteristics (ResearchStudy.enrollment
    /// -> Group.characteristic), with their Libraries looked up among the included resources.
    /// </summary>
    public static List<CriterionDefinitionDto> ToCriteria(
        Group? group,
        IReadOnlyDictionary<string, Library> librariesById
    ) =>
        [
            .. (group?.Characteristic ?? [])
                .Where(c => !string.IsNullOrEmpty(c.Code?.Text))
                .Select(c =>
                {
                    // Prefer the Library's own (current) title over Code.Text, a snapshot of the
                    // criterion's display text taken when the characteristic was authored - the two
                    // can drift once a Library is renamed, or reused with a different label, after
                    // the fact. Sql is resolved from the same Library lookup, so this falls back to
                    // Code.Text only when the reference doesn't resolve to a Library we fetched.
                    var library = (c.Value as ResourceReference).GetReferencedId() is { } libraryId
                        ? librariesById.GetValueOrDefault(libraryId)
                        : null;
                    return new CriterionDefinitionDto
                    {
                        DisplayText = library?.Title ?? c.Code!.Text!,
                        Exclude = c.Exclude ?? false,
                        Sql = library?.GetSqlText(),
                    };
                }),
        ];

    /// <summary>Whether a screening-list entry is flagged as system-determined-ineligible.</summary>
    private static bool IsFlaggedIneligible(FhirList.EntryComponent entry) =>
        entry.Flag?.Coding?.Any(c =>
            c.System == FhirVocabulary.SystemDeterminedSubjectStatus
            && c.Code == FhirVocabulary.DeterminedStatusIneligible
        ) ?? false;

    private static string? TelecomValue(Patient? patient, ContactPoint.ContactPointSystem system) =>
        patient?.Telecom?.FirstOrDefault(t => t.System == system)?.Value;
}
