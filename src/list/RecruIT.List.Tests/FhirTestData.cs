using De.Medizininformatikinitiative.Kerndatensatz.Studie;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using RecruIT.List.Models;
using RecruIT.List.Services.Fhir;
using FhirList = Hl7.Fhir.Model.List;
using ListFhirConstants = RecruIT.List.Services.Fhir.FhirConstants;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Tests;

/// <summary>
/// Builds the screening-list resources the query module writes (ResearchStudy, its List of
/// ResearchSubject entries, their Patients), under ids unique to one test - see
/// FhirServerCollection - and writes them to the server in one transaction.
/// </summary>
internal sealed class FhirTestData(FhirClient client)
{
    public const string TrialIdentifierSystem = "https://fhir.example.org/study-id";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private int _count;

    /// <summary>A FHIR id (or identifier value) no other test uses.</summary>
    public string Id(string name) => $"{name}-{_suffix}";

    public static TrialIdentifier TrialOf(ResearchStudy study) =>
        study.GetTrialIdentifier() ?? throw new InvalidOperationException("No trial identifier.");

    public ResearchStudy Study(string acronym, string? title = null)
    {
        var study = new ResearchStudy
        {
            Id = Id($"study-{acronym.ToLowerInvariant()}"),
            Status = ResearchStudy.ResearchStudyStatus.Active,
            Title = title ?? $"{acronym} trial",
            Description = $"About {acronym}",
        };
        study.Identifier.Add(new Identifier(TrialIdentifierSystem, Id(acronym)));
        study.Extension.Add(Studie.Extensions.MiiExStudieAkronym(new FhirString(acronym)));
        return study;
    }

    public Patient Patient(string given, string family, string? birthDate = "1970-01-01")
    {
        var patient = new Patient
        {
            Id = Id($"patient-{given.ToLowerInvariant()}"),
            Name = [new HumanName { Given = [given], Family = family }],
            Gender = AdministrativeGender.Female,
            BirthDate = birthDate,
        };
        patient.Identifier.Add(
            new Identifier("https://fhir.example.org/mrn", Id($"mrn-{given.ToLowerInvariant()}"))
            {
                Type = new CodeableConcept(
                    ListFhirConstants.SystemIdentifierType,
                    ListFhirConstants.IdentifierTypeMedicalRecordNumber
                ),
            }
        );
        return patient;
    }

    public ResearchSubject Subject(
        ResearchStudy study,
        Patient patient,
        ResearchSubject.ResearchSubjectStatus status =
            ResearchSubject.ResearchSubjectStatus.Candidate
    )
    {
        var id = Id($"subject-{++_count}");
        return new ResearchSubject
        {
            Id = id,
            Status = status,
            Study = new ResourceReference($"ResearchStudy/{study.Id}"),
            Individual = new ResourceReference($"Patient/{patient.Id}"),
            Identifier = [new Identifier(ListFhirConstants.UrlResearchSubjectIdentifier, id)],
        };
    }

    public FhirList ScreeningList(
        ResearchStudy study,
        IEnumerable<(ResearchSubject Subject, DateTimeOffset RecommendedAt)> entries,
        FhirList.ListStatus status = FhirList.ListStatus.Current
    )
    {
        var list = new FhirList
        {
            Id = Id($"list-{++_count}"),
            Status = status,
            Mode = ListMode.Working,
            Code = new CodeableConcept(
                ListFhirConstants.SystemScreeningList,
                ListFhirConstants.ScreeningListCode
            ),
        };
        list.Extension.Add(
            new Extension(
                ListFhirConstants.UrlListBelongsToStudy,
                new ResourceReference($"ResearchStudy/{study.Id}")
            )
        );
        foreach (var (subject, recommendedAt) in entries)
        {
            list.Entry.Add(
                new FhirList.EntryComponent
                {
                    Item = new ResourceReference($"ResearchSubject/{subject.Id}"),
                    DateElement = new FhirDateTime(recommendedAt),
                }
            );
        }

        return list;
    }

    /// <summary>Writes the resources in one transaction, each as a PUT to its own id.</summary>
    public async Task PutAsync(params Resource[] resources)
    {
        var bundle = new Bundle { Type = Bundle.BundleType.Transaction };
        foreach (var resource in resources)
        {
            bundle.Entry.Add(
                new Bundle.EntryComponent
                {
                    Resource = resource,
                    Request = new Bundle.RequestComponent
                    {
                        Method = Bundle.HTTPVerb.PUT,
                        Url = $"{resource.TypeName}/{resource.Id}",
                    },
                }
            );
        }

        await client.TransactionAsync(bundle);
    }
}
