using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using RecruIT.List.Models;
using RecruIT.List.Services.Fhir;
using FhirList = Hl7.Fhir.Model.List;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Tests;

/// <summary>The Criteria Manager's round trip: author criteria, submit them, read them back.</summary>
[Collection(FhirServerCollection.Name)]
public sealed class EligibilityCriteriaServiceTests(FhirServerFixture fhir)
{
    private readonly FhirTestData _data = new(fhir.CreateClient());

    private EligibilityCriteriaService CreateService() =>
        new(
            fhir.CreateClientFactory(),
            new FakeStringLocalizer(),
            NullLogger<EligibilityCriteriaService>.Instance
        );

    /// <summary>Titles derive the resources' ids, so each test's must be its own.</summary>
    private ResearchStudyDraft NewStudyDraft(bool createEmptyScreeningList = false) =>
        new()
        {
            Title = $"Heart Failure Registry {_data.Id("")}",
            Acronym = "HFR",
            Description = "Adults with heart failure",
            CreateEmptyScreeningList = createEmptyScreeningList,
        };

    private CriterionDraft NewCriterion(string displayText, string sql) =>
        new()
        {
            DisplayText = $"{displayText} {_data.Id("")}",
            Sql = sql,
            Dialect = SqlDialect.Trino,
        };

    private static string StudyIdOf(EligibilityCriteriaService service, ResearchStudyDraft draft) =>
        System
            .Text.Json.JsonDocument.Parse(service.BuildPreviewJson(draft, []))
            .RootElement.GetProperty("entry")[0]
            .GetProperty("resource")
            .GetProperty("id")
            .GetString()!;

    [Fact]
    public async Task Submitted_criteria_can_be_loaded_back_for_editing()
    {
        var service = CreateService();
        var draft = NewStudyDraft();
        var authored = NewCriterion("Ejection fraction below 40%", "SELECT patient_id FROM ef");
        authored.Exclude = true;
        var ct = TestContext.Current.CancellationToken;

        await service.SubmitAsync(draft, [authored], ct);
        var loaded = Assert.Single(
            await service.GetStudyCriteriaAsync(StudyIdOf(service, draft), ct)
        );

        Assert.Equal(authored.DisplayText, loaded.DisplayText);
        Assert.True(loaded.Exclude);
        Assert.Equal("SELECT patient_id FROM ef", loaded.Sql);
        Assert.StartsWith("Library/", loaded.ExistingLibraryReference);
    }

    [Fact]
    public async Task The_library_catalog_shows_which_studies_use_each_library()
    {
        var service = CreateService();
        var criterion = NewCriterion("Adults", "SELECT patient_id FROM adults");
        var ct = TestContext.Current.CancellationToken;
        await service.SubmitAsync(NewStudyDraft(), [criterion], ct);

        var library = Assert.Single(
            await service.SearchLibrariesAsync(ct),
            l => l.Title == criterion.DisplayText
        );

        Assert.Equal("SELECT patient_id FROM adults", library.Sql);
        Assert.Equal(["trino"], library.SqlDialects);
        Assert.Equal(["HFR"], library.UsedByStudyDisplayNames);
        Assert.True(library.Matches(criterion.DisplayText.ToUpperInvariant()));
        Assert.False(library.Matches("no such criterion"));
    }

    [Fact]
    public async Task An_empty_screening_list_makes_the_study_show_up_before_its_first_poll()
    {
        var service = CreateService();
        var draft = NewStudyDraft(createEmptyScreeningList: true);
        var ct = TestContext.Current.CancellationToken;

        await service.SubmitAsync(draft, [NewCriterion("Adults", "SELECT 1")], ct);

        var studyId = StudyIdOf(service, draft);
        var lists = await FhirBundleHelpers.GetAllPagesAsync(
            fhir.CreateClient(),
            $"List?belongs-to-study=ResearchStudy/{studyId}",
            ct
        );
        var list = Assert.Single(lists.OfType<FhirList>());
        Assert.Empty(list.Entry);
        Assert.Equal(FhirList.ListStatus.Current, list.Status);
    }

    [Fact]
    public async Task An_existing_study_keeps_everything_but_its_enrolled_group()
    {
        var existing = _data.Study("EXISTING");
        var oldGroup = new Group
        {
            Id = _data.Id("old-group"),
            Type = Group.GroupType.Person,
            Actual = false,
        };
        existing.Enrollment = [new ResourceReference($"Group/{oldGroup.Id}")];
        await _data.PutAsync(oldGroup, existing);
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        var summary = Assert.Single(
            await service.GetResearchStudiesAsync(ct),
            s => s.Id == existing.Id
        );
        Assert.Equal(("EXISTING", existing.Title), (summary.Acronym, summary.Title));
        var criterion = NewCriterion("Adults", "SELECT 1");
        await service.SubmitAsync(
            new ResearchStudyDraft { Title = "ignored", ExistingStudyId = existing.Id },
            [criterion],
            ct
        );

        var stored = await fhir.CreateClient()
            .ReadAsync<ResearchStudy>($"ResearchStudy/{existing.Id}", ct: ct);
        Assert.Equal(existing.Title, stored?.Title);
        Assert.Equal(FhirTestData.TrialOf(existing), stored?.GetTrialIdentifier());
        var enrollment = Assert.Single(stored!.Enrollment);
        Assert.NotEqual($"Group/{oldGroup.Id}", enrollment.Reference);
        Assert.Equal(
            [criterion.DisplayText],
            (await service.GetStudyCriteriaAsync(existing.Id!, ct)).Select(c => c.DisplayText)
        );
    }

    [Fact]
    public async Task The_study_picker_reads_the_server_once_per_circuit()
    {
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;
        var first = await service.GetResearchStudiesAsync(ct);

        var added = _data.Study("LATER");
        await _data.PutAsync(added);

        Assert.Same(first, await service.GetResearchStudiesAsync(ct));
        Assert.Contains(await CreateService().GetResearchStudiesAsync(ct), s => s.Id == added.Id);
    }

    [Fact]
    public void Picking_a_study_that_was_never_listed_is_refused()
    {
        var draft = new ResearchStudyDraft
        {
            Title = "ignored",
            ExistingStudyId = _data.Id("unlisted"),
        };

        Assert.Throws<InvalidOperationException>(() => CreateService().BuildPreviewJson(draft, []));
    }

    [Fact]
    public async Task A_rejected_transaction_is_reported_as_a_submit_failure()
    {
        // A reused criterion pointing at a Library the server doesn't have.
        var dangling = new CriterionDraft
        {
            DisplayText = "Gone",
            ExistingLibraryReference = $"Library/{_data.Id("deleted")}",
        };

        var ex = await Assert.ThrowsAsync<FhirAccessException>(() =>
            CreateService()
                .SubmitAsync(NewStudyDraft(), [dangling], TestContext.Current.CancellationToken)
        );

        Assert.Equal("App.Errors.CriteriaSubmitFailed", ex.Message);
    }

    [Fact]
    public async Task Two_new_criteria_with_the_same_title_share_one_library()
    {
        var service = CreateService();
        var draft = NewStudyDraft();
        var first = NewCriterion("Adults", "SELECT 1");
        var second = new CriterionDraft
        {
            DisplayText = first.DisplayText,
            Sql = "SELECT 2",
            Exclude = true,
        };
        var ct = TestContext.Current.CancellationToken;

        await service.SubmitAsync(draft, [first, second], ct);

        var loaded = await service.GetStudyCriteriaAsync(StudyIdOf(service, draft), ct);
        Assert.Equal(2, loaded.Count);
        Assert.Single(loaded.Select(c => c.ExistingLibraryReference).Distinct());
    }
}
