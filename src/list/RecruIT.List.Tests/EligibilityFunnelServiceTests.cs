using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using RecruIT.FhirConstants;
using RecruIT.List.Services.Fhir;
using ListFhirConstants = RecruIT.List.Services.Fhir.FhirConstants;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Tests;

[Collection(FhirServerCollection.Name)]
public sealed class EligibilityFunnelServiceTests(FhirServerFixture fhir)
{
    private readonly FhirTestData _data = new(fhir.CreateClient());

    private EligibilityFunnelService CreateService() =>
        new(
            fhir.CreateClientFactory(),
            new FakeStringLocalizer(),
            NullLogger<EligibilityFunnelService>.Instance
        );

    private static MeasureReport.PopulationComponent Step(
        string? label,
        int? count,
        string? code = null
    ) =>
        new()
        {
            Code = code is null
                ? new CodeableConcept { Text = label }
                : new CodeableConcept(
                    Recruit.CodeSystems.Urls.EligibilityFunnelPopulationType,
                    code
                ),
            Count = count,
        };

    [Fact]
    public async Task The_funnel_lists_each_step_in_order_starting_with_the_total_population()
    {
        var study = _data.Study("FUNNEL");
        var report = new MeasureReport
        {
            Id = _data.Id("funnel"),
            Status = MeasureReport.MeasureReportStatus.Complete,
            Type = MeasureReport.MeasureReportType.Summary,
            Measure = "https://fhir.example.org/Measure/eligibility-funnel",
            Period = new Period { Start = "2026-01-01", End = "2026-03-01" },
            Group =
            [
                new MeasureReport.GroupComponent
                {
                    Population =
                    [
                        Step(
                            null,
                            1200,
                            Recruit.CodeSystems.EligibilityFunnelPopulationType.TotalPopulation.Code()
                        ),
                        Step("Older than 18", 950),
                        Step("Not yet counted", null),
                        Step("Diabetic", 120),
                    ],
                },
            ],
        };
        report.Extension.Add(
            new Extension(
                ListFhirConstants.UrlListBelongsToStudy,
                new ResourceReference($"ResearchStudy/{study.Id}")
            )
        );
        await _data.PutAsync(study, report);

        var funnel = await CreateService()
            .GetFunnelAsync(study.Id!, TestContext.Current.CancellationToken);

        Assert.NotNull(funnel);
        Assert.Equal(
            [((string?)null, 1200, true), ("Older than 18", 950, false), ("Diabetic", 120, false)],
            funnel.Select(s => (s.Label, s.Count, s.IsTotalPopulation))
        );
    }

    [Fact]
    public async Task A_study_without_a_published_funnel_has_none()
    {
        var study = _data.Study("NOFUNNEL");
        await _data.PutAsync(study);

        Assert.Null(
            await CreateService().GetFunnelAsync(study.Id!, TestContext.Current.CancellationToken)
        );
    }
}
