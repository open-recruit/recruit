using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using RecruIT.List.Services.Fhir;
using ListFhirConstants = RecruIT.List.Services.Fhir.FhirConstants;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Tests;

[Collection(FhirServerCollection.Name)]
public sealed class PatientRecordServiceTests(FhirServerFixture fhir)
{
    private const string Snomed = "http://snomed.info/sct";

    private readonly FhirTestData _data = new(fhir.CreateClient());

    private PatientRecordService CreateService() =>
        new(
            fhir.CreateClientFactory(),
            new FakeStringLocalizer(),
            NullLogger<PatientRecordService>.Instance
        );

    private Encounter EncounterOf(Patient patient, string name, string start) =>
        new()
        {
            Id = _data.Id(name),
            Status = Encounter.EncounterStatus.Finished,
            Class = new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "IMP"),
            Subject = new ResourceReference($"Patient/{patient.Id}"),
            Period = new Period { Start = start },
        };

    private static Encounter.LocationComponent At(string reference) =>
        new() { Location = new ResourceReference(reference) };

    [Fact]
    public async Task The_latest_location_is_the_newest_encounters_ward()
    {
        var patient = _data.Patient("Erika", "Mustermann");
        var cardiology = new Location { Id = _data.Id("cardiology"), Name = "Cardiology ward" };
        var surgery = new Location { Id = _data.Id("surgery"), Name = "Surgery ward" };
        var older = EncounterOf(patient, "older", "2026-01-10T08:00:00+01:00");
        older.Location = [At($"Location/{surgery.Id}")];
        var newest = EncounterOf(patient, "newest", "2026-03-02T09:30:00+01:00");
        newest.Location = [At($"Location/{cardiology.Id}")];
        await _data.PutAsync(patient, cardiology, surgery, older, newest);

        var location = await CreateService()
            .GetLatestLocationAsync(patient.Id!, TestContext.Current.CancellationToken);

        Assert.Equal("Cardiology ward", location?.LocationName);
        Assert.Equal(DateTimeOffset.Parse("2026-03-02T09:30:00+01:00"), location?.EncounterStart);
    }

    [Fact]
    public async Task Encounters_without_any_location_are_skipped()
    {
        var patient = _data.Patient("Max", "Mustermann");
        var withProvider = EncounterOf(patient, "with-provider", "2026-01-10T08:00:00Z");
        withProvider.ServiceProvider = new ResourceReference { Display = "Uniklinik" };
        var withoutLocation = EncounterOf(patient, "without-location", "2026-03-02T08:00:00Z");
        await _data.PutAsync(patient, withProvider, withoutLocation);

        var location = await CreateService()
            .GetLatestLocationAsync(patient.Id!, TestContext.Current.CancellationToken);

        Assert.Equal("Uniklinik", location?.LocationName);
    }

    [Fact]
    public async Task A_location_known_only_by_its_display_text_is_used_as_is()
    {
        var patient = _data.Patient("Erika", "Display");
        var encounter = EncounterOf(patient, "display-only", "2026-02-01T08:00:00Z");
        encounter.Location = [new() { Location = new ResourceReference { Display = "ICU 3" } }];
        await _data.PutAsync(patient, encounter);

        var location = await CreateService()
            .GetLatestLocationAsync(patient.Id!, TestContext.Current.CancellationToken);

        Assert.Equal("ICU 3", location?.LocationName);
    }

    [Fact]
    public async Task A_patient_without_encounters_has_no_location()
    {
        var patient = _data.Patient("Nobody", "Visited");
        await _data.PutAsync(patient);

        Assert.Null(
            await CreateService()
                .GetLatestLocationAsync(patient.Id!, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Criteria_statuses_are_read_from_the_trials_eligibility_observations()
    {
        var study = _data.Study("CRITERIA");
        var otherStudy = _data.Study("OTHER");
        var patient = _data.Patient("Erika", "Criteria");
        await _data.PutAsync(
            study,
            otherStudy,
            patient,
            Assessment(patient, study, "Older than 18", "373066001"),
            Assessment(patient, study, "Diabetic", "373067005"),
            Assessment(patient, study, "BMI below 30", "82334004", note: "no recent BMI"),
            Assessment(patient, study, "Consented", "261665006"), // "Unknown"
            Assessment(patient, otherStudy, "Some other trial's criterion", "373066001")
        );

        var statuses = await CreateService()
            .GetEligibilityCriteriaStatusAsync(
                patient.Id!,
                FhirTestData.TrialOf(study),
                TestContext.Current.CancellationToken
            );

        Assert.Equal(
            [
                ("BMI below 30", (bool?)null, true, "no recent BMI"),
                ("Consented", null, false, null),
                ("Diabetic", false, false, null),
                ("Older than 18", true, false, null),
            ],
            statuses.Select(s => (s.DisplayText, s.Met, s.Indeterminate, s.Note))
        );
    }

    private Observation Assessment(
        Patient patient,
        ResearchStudy study,
        string criterion,
        string snomedCode,
        string? note = null
    ) =>
        new()
        {
            Id = _data.Id($"assessment-{Guid.NewGuid():N}"[..24]),
            Status = ObservationStatus.Final,
            Category =
            [
                new CodeableConcept(
                    ListFhirConstants.SystemObservationCategory,
                    ListFhirConstants.ObservationCategoryEligibilityAssessment
                ),
            ],
            Code = new CodeableConcept { Text = criterion },
            Subject = new ResourceReference($"Patient/{patient.Id}"),
            Focus = [new ResourceReference($"ResearchStudy/{study.Id}")],
            Value = new CodeableConcept(Snomed, snomedCode) { Text = note },
        };
}
