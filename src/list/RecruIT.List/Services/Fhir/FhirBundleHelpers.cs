using System.Text;
using De.Medizininformatikinitiative.Kerndatensatz.Studie;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using RecruIT.List.Models;

namespace RecruIT.List.Services.Fhir;

internal static class FhirBundleHelpers
{
    /// <summary>
    /// Issues a GET against the given relative FHIR url and follows "next" links, returning
    /// every resource across all pages. Mirrors the "pageLimit: 0" behavior list-old relied on.
    /// </summary>
    public static async Task<List<Resource>> GetAllPagesAsync(
        FhirClient client,
        string relativeUrl,
        CancellationToken ct = default
    )
    {
        var resources = new List<Resource>();

        var result = await client.GetAsync(relativeUrl, ct);
        var bundle = result as Bundle;

        while (bundle is not null)
        {
            resources.AddRange(
                bundle.Entry.Where(e => e.Resource is not null).Select(e => e.Resource!)
            );

            if (bundle.NextLink is null)
            {
                break;
            }

            bundle = await client.ContinueAsync(bundle, PageDirection.Next, ct);
        }

        return resources;
    }

    public static ResourceReference? GetReferenceExtension(this IExtendable element, string url) =>
        element.GetExtension(url)?.Value as ResourceReference;

    /// <summary>Extracts the bare logical id (e.g. "abc123") from a relative reference (e.g. "ResearchStudy/abc123").</summary>
    public static string? GetReferencedId(this ResourceReference? reference) =>
        reference?.Reference?.Split('/').LastOrDefault();

    public static TrialIdentifier? GetTrialIdentifier(this ResearchStudy study) =>
        study.Identifier?.FirstOrDefault(i =>
            !string.IsNullOrEmpty(i.System) && !string.IsNullOrEmpty(i.Value)
        )
            is { System: { Length: > 0 } system, Value: { Length: > 0 } value }
            ? new TrialIdentifier(system, value)
            : null;

    /// <summary>
    /// ResearchSubject's business identifier formatted as "system|value" - see
    /// RecruIT.List.Services.Fhir.FhirConstants.UrlResearchSubjectIdentifier. Null for subjects that predate that identifier
    /// being set by query-sql-on-fhir.
    /// </summary>
    public static string? GetResearchSubjectIdentifierToken(this ResearchSubject subject) =>
        subject.Identifier?.FirstOrDefault(i =>
            i.System == FhirConstants.UrlResearchSubjectIdentifier
        )
            is { System: { Length: > 0 } system, Value: { Length: > 0 } value }
            ? $"{system}|{value}"
            : null;

    /// <summary>Parses a FHIR instant/date/dateTime string, or null if absent/unparsable.</summary>
    public static DateTimeOffset? ParseFhirInstant(string? value) =>
        value is not null && DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    public static string GetStudyAcronym(this ResearchStudy study) =>
        study.GetMiiExStudieAkronym()?.Value is { Length: > 0 } acronym ? acronym : string.Empty;

    /// <summary>Decodes a criterion Library's SQL source from its content attachment.</summary>
    public static string? GetSqlText(this Library library) =>
        library.Content?.FirstOrDefault()?.Data is { Length: > 0 } data
            ? Encoding.UTF8.GetString(data)
            : null;

    /// <summary>
    /// The dialects a criterion Library has SQL content for, in content order - see SqlContentType.
    /// </summary>
    public static IReadOnlyList<string> GetSqlDialects(this Library library) =>
        [
            .. library
                .Content.Select(c => SqlContentType.DialectOf(c.ContentType))
                .OfType<string>()
                .Distinct(),
        ];

    public static string? GetMedicalRecordNumber(this Patient patient) =>
        patient.Identifier.FirstOrDefault(IsMedicalRecordNumber)?.Value;

    /// <summary>
    /// How a patient is labelled everywhere in the UI and in notifications: Patient.name, else the
    /// MR (medical record number) identifier, else the first identifier, else the logical id.
    /// Takes the id separately since the Patient itself may be missing from a search bundle.
    /// </summary>
    public static string GetPatientDisplayName(Patient? patient, string patientId) =>
        FormatHumanName(patient?.Name?.FirstOrDefault())
        ?? patient?.GetMedicalRecordNumber()
        ?? patient?.Identifier.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Value))?.Value
        ?? patientId;

    private static string? FormatHumanName(HumanName? name)
    {
        if (name is null)
        {
            return null;
        }

        var formatted = string.Join(
            ' ',
            new[] { string.Join(' ', name.Given ?? []), name.Family }.Where(s =>
                !string.IsNullOrWhiteSpace(s)
            )
        );
        return formatted.Length > 0 ? formatted
            : string.IsNullOrWhiteSpace(name.Text) ? null
            : name.Text;
    }

    private static bool IsMedicalRecordNumber(Identifier identifier) =>
        identifier.Type?.Coding.Any(c =>
            c.System == FhirConstants.SystemIdentifierType
            && c.Code == FhirConstants.IdentifierTypeMedicalRecordNumber
        )
            is true;
}
