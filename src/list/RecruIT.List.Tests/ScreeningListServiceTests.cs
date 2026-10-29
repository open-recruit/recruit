using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using RecruIT.List.Models;
using RecruIT.List.Services.Access;
using RecruIT.List.Services.Fhir;
using static RecruIT.List.Tests.TestUsers;
using FhirList = Hl7.Fhir.Model.List;
using ListFhirConstants = RecruIT.List.Services.Fhir.FhirConstants;
using Status = Hl7.Fhir.Model.ResearchSubject.ResearchSubjectStatus;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Tests;

[Collection(FhirServerCollection.Name)]
public sealed class ScreeningListServiceTests(FhirServerFixture fhir) : IDisposable
{
    private const string ViewerEmail = "viewer@example.com";

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly SqliteDbContextFactory _db = new();
    private readonly FhirTestData _data = new(fhir.CreateClient());

    public void Dispose() => _db.Dispose();

    private ScreeningListService CreateService() =>
        new(
            fhir.CreateClientFactory(),
            new TrialAccessService(
                _db,
                new FakeStringLocalizer(),
                NullLogger<TrialAccessService>.Instance
            ),
            new FakeStringLocalizer(),
            NullLogger<ScreeningListService>.Instance
        );

    private System.Security.Claims.ClaimsPrincipal Viewer() =>
        CreateUser(sub: _data.Id("viewer"), email: ViewerEmail);

    private Task GrantViewerAsync(ResearchStudy study) =>
        SeedGrantAsync(
            _db,
            FhirTestData.TrialOf(study),
            ViewerEmail,
            TrialPermissionLevel.Viewer,
            _data.Id("viewer")
        );

    /// <summary>One trial whose list has a subject in every status, and its patients.</summary>
    private async Task<(ResearchStudy Study, FhirList List)> SeedTrialAsync(
        string acronym,
        params Status[] statuses
    )
    {
        var study = _data.Study(acronym);
        var resources = new List<Resource> { study };
        var entries = new List<(ResearchSubject, DateTimeOffset)>();
        for (var i = 0; i < statuses.Length; i++)
        {
            var patient = _data.Patient($"{acronym}Patient{i}", "Doe");
            var subject = _data.Subject(study, patient, statuses[i]);
            resources.AddRange([patient, subject]);
            entries.Add((subject, Now.AddDays(-i)));
        }

        var list = _data.ScreeningList(study, entries);
        resources.Add(list);
        await _data.PutAsync([.. resources]);
        return (study, list);
    }

    [Fact]
    public async Task Trials_are_counted_by_recruitment_status()
    {
        var (study, list) = await SeedTrialAsync(
            "COUNT",
            Status.OnStudy,
            Status.Candidate,
            Status.Screening,
            Status.Ineligible,
            Status.Withdrawn
        );
        await GrantViewerAsync(study);

        var trial = Assert.Single(
            await CreateService()
                .GetAccessibleTrialsAsync(Viewer(), TestContext.Current.CancellationToken)
        );

        Assert.Equal(list.Id, trial.ListId);
        Assert.Equal(study.Id, trial.StudyId);
        Assert.Equal("COUNT", trial.StudyAcronym);
        Assert.Equal(FhirTestData.TrialOf(study), trial.TrialIdentifier);
        Assert.Equal(
            (1, 2, 2),
            (trial.RecruitedCount, trial.PendingCount, trial.NotRecruitedCount)
        );
        Assert.Equal("current", trial.ListStatus);
    }

    [Fact]
    public async Task Users_only_see_the_trials_they_were_granted()
    {
        var (granted, _) = await SeedTrialAsync("GRANTED", Status.Candidate);
        var (other, _) = await SeedTrialAsync("OTHER", Status.Candidate);
        await GrantViewerAsync(granted);
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        var viewerTrials = await service.GetAccessibleTrialsAsync(Viewer(), ct);
        var adminTrials = await service.GetAccessibleTrialsAsync(
            CreateUser(sub: _data.Id("admin"), isAdmin: true),
            ct
        );

        Assert.Equal([granted.Id], viewerTrials.Select(t => t.StudyId));
        Assert.Contains(adminTrials, t => t.StudyId == granted.Id);
        Assert.Contains(adminTrials, t => t.StudyId == other.Id);
    }

    [Fact]
    public async Task The_dashboard_includes_retired_lists_but_the_patient_overview_does_not()
    {
        var study = _data.Study("RETIRED");
        var patient = _data.Patient("Retiree", "Doe");
        var subject = _data.Subject(study, patient);
        await _data.PutAsync(
            study,
            patient,
            subject,
            _data.ScreeningList(study, [(subject, Now)], FhirList.ListStatus.Retired)
        );
        await GrantViewerAsync(study);
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(
            "retired",
            Assert.Single(await service.GetAccessibleTrialsAsync(Viewer(), ct)).ListStatus
        );
        Assert.Empty(await service.GetPatientsAcrossTrialsAsync(Viewer(), ct));
    }

    [Fact]
    public async Task A_patient_on_several_lists_shows_up_once_with_each_trial()
    {
        var beta = _data.Study("BETA");
        var alpha = _data.Study("ALPHA");
        var patient = _data.Patient("Erika", "Mustermann");
        var inBeta = _data.Subject(beta, patient, Status.Screening);
        var inAlpha = _data.Subject(alpha, patient);
        await _data.PutAsync(
            beta,
            alpha,
            patient,
            inBeta,
            inAlpha,
            _data.ScreeningList(beta, [(inBeta, Now)]),
            _data.ScreeningList(alpha, [(inAlpha, Now)])
        );
        await GrantViewerAsync(alpha);
        await GrantViewerAsync(beta);

        var overview = Assert.Single(
            await CreateService()
                .GetPatientsAcrossTrialsAsync(Viewer(), TestContext.Current.CancellationToken)
        );

        Assert.Equal(patient.Id, overview.PatientId);
        Assert.Equal("Erika Mustermann", overview.DisplayName);
        Assert.Equal(["ALPHA", "BETA"], overview.Trials.Select(t => t.StudyAcronym));
        Assert.Equal(["candidate", "screening"], overview.Trials.Select(t => t.Status));
    }

    [Fact]
    public async Task A_lists_patients_come_newest_recommendation_first_with_their_details()
    {
        var study = _data.Study("DETAILS");
        var older = _data.Patient("Older", "Doe");
        older.Telecom =
        [
            new ContactPoint(ContactPoint.ContactPointSystem.Phone, null, "+49 123"),
            new ContactPoint(ContactPoint.ContactPointSystem.Email, null, "older@example.com"),
        ];
        var newer = _data.Patient("Newer", "Doe");
        var olderSubject = _data.Subject(study, older);
        var newerSubject = _data.Subject(study, newer, Status.OnStudy);
        var list = _data.ScreeningList(
            study,
            [(olderSubject, Now.AddDays(-3)), (newerSubject, Now.AddDays(-1))]
        );
        list.Entry[0].Flag = new CodeableConcept(
            ListFhirConstants.SystemDeterminedSubjectStatus,
            ListFhirConstants.DeterminedStatusIneligible
        );
        await _data.PutAsync(study, older, newer, olderSubject, newerSubject, list);
        await GrantViewerAsync(study);

        var (summary, patients) = await CreateService()
            .GetListWithPatientsAsync(list.Id!, Viewer(), TestContext.Current.CancellationToken);

        Assert.Equal(
            ("DETAILS", "About DETAILS"),
            (summary.StudyAcronym, summary.StudyDescription)
        );
        Assert.Equal(
            (1, 1, 0),
            (summary.RecruitedCount, summary.PendingCount, summary.NotRecruitedCount)
        );
        Assert.Equal(["Newer Doe", "Older Doe"], patients.Select(p => p.DisplayName));
        var olderEntry = patients[1];
        Assert.Equal(older.GetMedicalRecordNumber(), olderEntry.MedicalRecordNumber);
        Assert.Equal(("+49 123", "older@example.com"), (olderEntry.Phone, olderEntry.Email));
        Assert.True(olderEntry.SystemDeterminedIneligible);
        Assert.False(patients[0].SystemDeterminedIneligible);
        Assert.Equal(
            $"{ListFhirConstants.UrlResearchSubjectIdentifier}|{olderSubject.Identifier[0].Value}",
            olderEntry.ResearchSubjectIdentifier
        );
    }

    private Library SqlLibrary(string name, string? title, string sql) =>
        new()
        {
            Id = _data.Id(name),
            Status = PublicationStatus.Active,
            Title = title,
            Type = new CodeableConcept(
                "http://terminology.hl7.org/CodeSystem/library-type",
                "logic-library"
            ),
            Content =
            [
                new Attachment
                {
                    ContentType = "application/sql",
                    Data = System.Text.Encoding.UTF8.GetBytes(sql),
                },
            ],
        };

    [Fact]
    public async Task A_lists_criteria_come_from_the_studys_enrolled_group()
    {
        // The Library's own title wins over the characteristic's snapshot of it...
        var library = SqlLibrary("library-adults", "Adults (current title)", "SELECT 1");
        // ...which is only the fallback for a Library without one.
        var untitled = SqlLibrary("library-pregnant", title: null, "SELECT 2");
        var group = new Group
        {
            Id = _data.Id("group"),
            Type = Group.GroupType.Person,
            Actual = false,
            Characteristic =
            [
                new Group.CharacteristicComponent
                {
                    Code = new CodeableConcept { Text = "Adults (old snapshot)" },
                    Value = new ResourceReference($"Library/{library.Id}"),
                    Exclude = false,
                },
                new Group.CharacteristicComponent
                {
                    Code = new CodeableConcept { Text = "Pregnant" },
                    Value = new ResourceReference($"Library/{untitled.Id}"),
                    Exclude = true,
                },
            ],
        };
        var study = _data.Study("CRITERIA");
        study.Enrollment = [new ResourceReference($"Group/{group.Id}")];
        var list = _data.ScreeningList(study, []);
        await _data.PutAsync(library, untitled, group, study, list);
        await GrantViewerAsync(study);

        var (summary, patients) = await CreateService()
            .GetListWithPatientsAsync(list.Id!, Viewer(), TestContext.Current.CancellationToken);

        Assert.Empty(patients);
        Assert.Collection(
            summary.Criteria,
            c =>
                Assert.Equal(
                    ("Adults (current title)", false, "SELECT 1"),
                    (c.DisplayText, c.Exclude, c.Sql)
                ),
            c => Assert.Equal(("Pregnant", true, "SELECT 2"), (c.DisplayText, c.Exclude, c.Sql))
        );
    }

    [Fact]
    public async Task A_list_of_a_trial_the_user_was_not_granted_is_refused()
    {
        var (_, list) = await SeedTrialAsync("REFUSED", Status.Candidate);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CreateService()
                .GetListWithPatientsAsync(list.Id!, Viewer(), TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task An_unknown_list_is_reported_as_not_found()
    {
        var ex = await Assert.ThrowsAsync<FhirAccessException>(() =>
            CreateService()
                .GetListWithPatientsAsync(
                    _data.Id("no-such-list"),
                    Viewer(),
                    TestContext.Current.CancellationToken
                )
        );

        Assert.Equal("App.Errors.ListNotFound", ex.Message);
    }

    [Fact]
    public async Task Only_admins_may_retire_a_list()
    {
        var (study, list) = await SeedTrialAsync("STATUS", Status.Candidate);
        await GrantViewerAsync(study);
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateListStatusAsync(list.Id!, "retired", Viewer(), ct)
        );
        await service.UpdateListStatusAsync(
            list.Id!,
            "retired",
            CreateUser(sub: _data.Id("admin"), isAdmin: true),
            ct
        );

        var stored = await fhir.CreateClient().ReadAsync<FhirList>($"List/{list.Id}", ct: ct);
        Assert.Equal(FhirList.ListStatus.Retired, stored?.Status);
    }

    [Fact]
    public async Task A_trial_identifier_resolves_to_its_list()
    {
        var (study, list) = await SeedTrialAsync("RESOLVE", Status.Candidate);
        var token = FhirTestData.TrialOf(study).ToToken();
        var unknown = new TrialIdentifier(
            FhirTestData.TrialIdentifierSystem,
            _data.Id("unknown")
        ).ToToken();

        var resolved = await CreateService()
            .ResolveListsForTrialsAsync(
                [token, unknown, token],
                TestContext.Current.CancellationToken
            );

        Assert.Equal(2, resolved.Count);
        Assert.Equal((list.Id!, "RESOLVE"), resolved[token]);
        Assert.Null(resolved[unknown]);
    }
}
