using RecruIT.List.Models;
using RecruIT.List.Resources;

namespace RecruIT.List.Tests;

public sealed class CriterionStateDisplayTests
{
    private static CriterionStatusDto Criterion(bool? met, bool indeterminate = false) =>
        new()
        {
            DisplayText = "criterion",
            Met = met,
            Indeterminate = indeterminate,
        };

    [Fact]
    public void Counts_list_what_rules_a_patient_out_first_and_leave_out_empty_states()
    {
        var counts = CriterionStateDisplay.CountByState([
            Criterion(true),
            Criterion(null, indeterminate: true),
            Criterion(true),
            Criterion(false),
        ]);

        Assert.Equal(
            [
                (CriterionState.NotMet, 1),
                (CriterionState.Indeterminate, 1),
                (CriterionState.Met, 2),
            ],
            counts
        );
    }

    [Theory]
    [InlineData(true, false, CriterionState.Met)]
    [InlineData(true, true, CriterionState.Met)]
    [InlineData(false, false, CriterionState.NotMet)]
    [InlineData(null, true, CriterionState.Indeterminate)]
    [InlineData(null, false, CriterionState.Unknown)]
    public void A_criterions_state_follows_met_then_indeterminate(
        bool? met,
        bool indeterminate,
        CriterionState expected
    ) => Assert.Equal(expected, CriterionStateDisplay.Of(met, indeterminate));

    [Fact]
    public void Open_criteria_are_everything_not_yet_met()
    {
        var patient = new PatientListEntryDto
        {
            ResearchSubjectId = "subject",
            PatientId = "patient",
            DisplayName = "Erika",
            Status = "candidate",
        };
        Assert.Null(patient.OpenCriteriaCount);

        patient.CriteriaStatuses =
        [
            Criterion(true),
            Criterion(false),
            Criterion(null),
            Criterion(null, true),
        ];

        Assert.Equal(3, patient.OpenCriteriaCount);
    }
}
