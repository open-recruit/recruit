using System.Security.Cryptography;
using System.Text;
using De.Medizininformatikinitiative.Kerndatensatz.Studie;
using Hl7.Fhir.Model;
using RecruIT.List.Models;
using FhirList = Hl7.Fhir.Model.List;

namespace RecruIT.List.Services.Fhir;

/// <summary>
/// Assembles the FHIR resources a study's eligibility criteria consist of, per
/// docs/trino/eligibility-criteria-design.md, into the transaction Bundle the Criteria Manager
/// previews and EligibilityCriteriaService submits. Bundle shape: one Library entry per
/// newly-authored criterion, one Group entry referencing all of them (plus any reused-by-reference
/// criteria) via Group.characteristic, one ResearchStudy entry enrolling that Group, and optionally
/// an empty screening List. Every entry is a PUT to a deterministic update-as-create id, so
/// re-submitting the same draft targets the same resources rather than duplicating them.
/// </summary>
public static class EligibilityCriteriaBundleBuilder
{
    /// <summary>
    /// Library.type coding that marks a Library as SQL-on-FHIR eligibility-criterion SQL, as
    /// opposed to any other kind of Library resource a FHIR server might hold - see
    /// query-sql-on-fhir's PollForStudies.SQL_QUERY_LIBRARY_TYPE_SYSTEM/_CODE, which writes this
    /// same coding.
    /// </summary>
    public const string SqlQueryLibraryTypeSystem =
        "https://sql-on-fhir.org/ig/CodeSystem/LibraryTypesCodes";

    public const string SqlQueryLibraryTypeCode = "sql-query";

    /// <summary>
    /// SQL-on-FHIR's own extension for carrying the SQL text alongside content.data's base64 - see
    /// https://build.fhir.org/ig/FHIR/sql-on-fhir-v2/StructureDefinition-SQLQuery.html#sql-attachments.
    /// </summary>
    private const string SqlTextExtensionUrl =
        "https://sql-on-fhir.org/ig/StructureDefinition/sql-text";

    /// <param name="studyDraft">The study the criteria are authored for.</param>
    /// <param name="criteria">The criteria, in characteristic order.</param>
    /// <param name="existingStudy">
    /// The ResearchStudy picked via the "use an existing study" flow, exactly as last fetched -
    /// required when studyDraft.ExistingStudyId is set, ignored otherwise.
    /// </param>
    public static Bundle Build(
        ResearchStudyDraft studyDraft,
        IReadOnlyList<CriterionDraft> criteria,
        ResearchStudy? existingStudy = null
    )
    {
        var libraryEntries = new List<Bundle.EntryComponent>();
        var libraryIds = new HashSet<string>();
        var characteristics = new List<Group.CharacteristicComponent>();

        foreach (var criterion in criteria)
        {
            ResourceReference libraryReference;

            if (!string.IsNullOrWhiteSpace(criterion.ExistingLibraryReference))
            {
                libraryReference = new ResourceReference(criterion.ExistingLibraryReference.Trim());
            }
            else
            {
                // Library.name is always the slugified Title, never independently authored - so its
                // identity (and thus the update-as-create hash below) is purely a function of Title.
                // Two ad hoc criteria in the same study sharing a Title collide into one Library by
                // design, same as the "point a second study's characteristic at an existing Library"
                // dedup story in docs/trino/eligibility-criteria-design.md - the first one defines
                // it, since a transaction can't PUT the same resource twice.
                var name = Slug.Create(criterion.DisplayText);
                var libraryId = ComputeIdentifierHashId(
                    FhirConstants.UrlUiCreatedEligibilityLibraryIdentifier,
                    name
                );
                libraryReference = new ResourceReference($"Library/{libraryId}");
                if (libraryIds.Add(libraryId))
                {
                    libraryEntries.Add(PutEntry(BuildLibrary(criterion, libraryId, name)));
                }
            }

            characteristics.Add(Characteristic(criterion, libraryReference));
        }

        var group = new Group
        {
            Type = Group.GroupType.Person,
            Actual = false,
            Active = true,
            Characteristic = characteristics,
        };

        // A UI-assigned identifier that anchors the Group's identity, shared with the ResearchStudy
        // below when it's newly-authored (each under their own resource-specific system, so they
        // still hash to different ids) - same story as the ad hoc Library criteria above. For a
        // study picked via the "use an existing study" flow, its own FHIR id anchors the Group's
        // identity instead, so re-authoring criteria for that study always targets the same Group.
        var groupIdentifierValue = studyDraft.ExistingStudyId ?? Slug.Create(studyDraft.Title);

        group.Identifier.Add(
            new Identifier(FhirConstants.UrlEligibilityGroupIdentifier, groupIdentifierValue)
        );
        group.Id = ComputeIdentifierHashId(
            FhirConstants.UrlEligibilityGroupIdentifier,
            groupIdentifierValue
        );
        var groupReference = new ResourceReference($"Group/{group.Id}");

        var study = studyDraft.ExistingStudyId is { } existingStudyId
            ? EnrollExistingStudy(existingStudyId, existingStudy, groupReference)
            : BuildNewStudy(studyDraft, groupReference);

        var bundle = new Bundle { Type = Bundle.BundleType.Transaction };
        bundle.Entry.Add(PutEntry(study));
        bundle.Entry.Add(PutEntry(group));
        bundle.Entry.AddRange(libraryEntries);

        if (studyDraft.CreateEmptyScreeningList)
        {
            bundle.Entry.Add(PutEntry(BuildEmptyScreeningList(study)));
        }

        return bundle;
    }

    private static Library BuildLibrary(CriterionDraft criterion, string libraryId, string name)
    {
        var library = new Library
        {
            Id = libraryId,
            Status = PublicationStatus.Active,
            Title = criterion.DisplayText,
            Name = name,
            Description = string.IsNullOrWhiteSpace(criterion.Description)
                ? null
                : criterion.Description,
            // Must match the type SearchLibrariesAsync filters on and PollForStudies'
            // chained discovery search looks for - without it this Library would be
            // invisible to both the catalog and the poll job once actually persisted.
            Type = new CodeableConcept(SqlQueryLibraryTypeSystem, SqlQueryLibraryTypeCode),
            // Same shape as superset-library-sync's Libraries (see SqlContentType): the
            // dialect-agnostic content first, which GetSqlText reads back, then the same SQL
            // for the criterion's dialect.
            Content =
            [
                BuildSqlAttachment(SqlContentType.DialectAgnostic, criterion.Sql),
                BuildSqlAttachment(SqlContentType.For(criterion.Dialect), criterion.Sql),
            ],
        };
        library.Identifier.Add(
            new Identifier(FhirConstants.UrlUiCreatedEligibilityLibraryIdentifier, name)
        );

        return library;
    }

    private static Group.CharacteristicComponent Characteristic(
        CriterionDraft criterion,
        ResourceReference libraryReference
    ) =>
        new()
        {
            Code = new CodeableConcept(system: null, code: null, text: criterion.DisplayText),
            Value = libraryReference,
            Exclude = criterion.Exclude,
        };

    private static Bundle.EntryComponent PutEntry(Resource resource) =>
        new()
        {
            Resource = resource,
            Request = new Bundle.RequestComponent
            {
                Method = Bundle.HTTPVerb.PUT,
                Url = $"{resource.TypeName}/{resource.Id}",
            },
        };

    private static Attachment BuildSqlAttachment(string contentType, string sql) =>
        new()
        {
            ContentType = contentType,
            Extension = [new Extension(SqlTextExtensionUrl, new FhirString(sql))],
            Data = Encoding.UTF8.GetBytes(sql),
        };

    /// <summary>
    /// The picked ResearchStudy exactly as last fetched, except that Enrollment is replaced with a
    /// single reference to the criteria Group - a study only ever screens against the one
    /// eligibility Group this page is authoring, so any previously-enrolled Group(s) (this page's
    /// own past output, or otherwise) are dropped rather than kept alongside it.
    /// </summary>
    private static ResearchStudy EnrollExistingStudy(
        string existingStudyId,
        ResearchStudy? existingStudy,
        ResourceReference groupReference
    )
    {
        if (existingStudy?.Id != existingStudyId)
        {
            throw new ArgumentException(
                $"The draft is for ResearchStudy/{existingStudyId}, but that study wasn't provided.",
                nameof(existingStudy)
            );
        }

        var study = existingStudy.DeepCopy();
        study.Enrollment = [groupReference];
        return study;
    }

    private static ResearchStudy BuildNewStudy(
        ResearchStudyDraft studyDraft,
        ResourceReference groupReference
    )
    {
        var study = new ResearchStudy
        {
            Status = ResearchStudy.ResearchStudyStatus.Active,
            Title = studyDraft.Title,
            Description = string.IsNullOrWhiteSpace(studyDraft.Description)
                ? null
                : studyDraft.Description,
            Enrollment = [groupReference],
        };

        if (!string.IsNullOrWhiteSpace(studyDraft.Acronym))
        {
            study.Extension.Add(
                Studie.Extensions.MiiExStudieAkronym(new FhirString(studyDraft.Acronym))
            );
        }

        // Always computable (Title is never null), so a newly-authored study always gets a
        // deterministic update-as-create id, never a plain POST with a server-assigned one.
        var uiCreatedIdentifierValue = Slug.Create(studyDraft.Title);
        study.Identifier.Add(
            new Identifier(
                FhirConstants.UrlUiCreatedResearchStudyIdentifier,
                uiCreatedIdentifierValue
            )
        );
        study.Id = ComputeIdentifierHashId(
            FhirConstants.UrlUiCreatedResearchStudyIdentifier,
            uiCreatedIdentifierValue
        );
        return study;
    }

    /// <summary>
    /// An empty (no entry) screening List for the given study, in the same shape and under the
    /// same update-as-create identity query-sql-on-fhir's EligibilityBundleBuilder uses - so a
    /// later successful poll updates this same List in place (adding its own entries) rather than
    /// creating a duplicate. Opt-in only (ResearchStudyDraft.CreateEmptyScreeningList): a study may
    /// already have a real List with recruited patients, and this would silently wipe its entries.
    /// </summary>
    private static FhirList BuildEmptyScreeningList(ResearchStudy study)
    {
        // Matches EligibilityBundleBuilder's own id/identifier derivation: the study's own first
        // business identifier value, or "" if it has none (same as Hapi's getIdentifierFirstRep()).
        var studyIdentifierValue = study.Identifier.FirstOrDefault()?.Value ?? string.Empty;

        var list = new FhirList
        {
            Status = FhirList.ListStatus.Current,
            Mode = ListMode.Working,
            Code = new CodeableConcept(
                FhirConstants.SystemScreeningList,
                FhirConstants.ScreeningListCode
            ),
        };
        list.Identifier.Add(
            new Identifier(FhirConstants.UrlScreeningListIdentifier, studyIdentifierValue)
        );
        list.Extension.Add(
            new Extension(
                FhirConstants.UrlListBelongsToStudy,
                new ResourceReference($"ResearchStudy/{study.Id}")
                {
                    Display = study.GetStudyDisplayName(),
                }
            )
        );
        list.Id = ComputeIdentifierHashId(
            FhirConstants.UrlScreeningListIdentifier,
            studyIdentifierValue
        );
        return list;
    }

    /// <summary>
    /// Update-as-create id for a resource identified by (system, value): a lowercase-hex SHA-256
    /// digest of "system|value", used as both the resource id and the PUT url so re-submitting the
    /// same identifier always targets the same resource instead of creating a duplicate.
    /// </summary>
    internal static string ComputeIdentifierHashId(string system, string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{system}|{value}")));
}
