using Hl7.Fhir.Model;
using RecruIT.List.Services.Fhir;
using ListFhirConstants = RecruIT.List.Services.Fhir.FhirConstants;

namespace RecruIT.List.Tests;

public sealed class PatientDisplayNameTests
{
    private static Identifier Mrn(string value) =>
        new()
        {
            Type = new CodeableConcept(
                ListFhirConstants.SystemIdentifierType,
                ListFhirConstants.IdentifierTypeMedicalRecordNumber
            ),
            Value = value,
        };

    private static readonly Identifier OtherIdentifier = new(
        "https://example.org/other",
        "OTHER-1"
    );

    [Fact]
    public void Prefers_the_human_name() =>
        Assert.Equal(
            "Jane Q Doe",
            FhirBundleHelpers.GetPatientDisplayName(
                new Patient
                {
                    Name = [new HumanName { Given = ["Jane", "Q"], Family = "Doe" }],
                    Identifier = [OtherIdentifier, Mrn("MRN-1")],
                },
                "pat-1"
            )
        );

    [Fact]
    public void Falls_back_to_the_name_text_when_given_and_family_are_absent() =>
        Assert.Equal(
            "Jane Doe",
            FhirBundleHelpers.GetPatientDisplayName(
                new Patient { Name = [new HumanName { Text = "Jane Doe" }] },
                "pat-1"
            )
        );

    [Fact]
    public void Falls_back_to_the_MR_identifier_over_an_earlier_identifier() =>
        Assert.Equal(
            "MRN-1",
            FhirBundleHelpers.GetPatientDisplayName(
                new Patient { Identifier = [OtherIdentifier, Mrn("MRN-1")] },
                "pat-1"
            )
        );

    [Fact]
    public void Falls_back_to_the_first_identifier_without_an_MR_identifier() =>
        Assert.Equal(
            "OTHER-1",
            FhirBundleHelpers.GetPatientDisplayName(
                new Patient { Identifier = [OtherIdentifier, new("https://example.org/b", "B")] },
                "pat-1"
            )
        );

    [Fact]
    public void Falls_back_to_the_logical_id() =>
        Assert.Equal("pat-1", FhirBundleHelpers.GetPatientDisplayName(new Patient(), "pat-1"));

    [Fact]
    public void Falls_back_to_the_logical_id_when_the_patient_is_missing() =>
        Assert.Equal("pat-1", FhirBundleHelpers.GetPatientDisplayName(null, "pat-1"));
}
