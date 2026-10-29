using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Logging.Abstractions;
using RecruIT.List.Models;
using RecruIT.List.Services;
using RecruIT.List.Services.Access;
using RecruIT.List.Services.Fhir;
using static RecruIT.List.Tests.TestUsers;
using Status = Hl7.Fhir.Model.ResearchSubject.ResearchSubjectStatus;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Tests;

[Collection(FhirServerCollection.Name)]
public sealed class ResearchSubjectServiceTests(FhirServerFixture fhir) : IDisposable
{
    private const string CoordinatorEmail = "coordinator@example.com";

    private readonly SqliteDbContextFactory _db = new();
    private readonly RecordedMetrics _metrics = new();
    private readonly FhirTestData _data = new(fhir.CreateClient());

    public void Dispose()
    {
        _metrics.Dispose();
        _db.Dispose();
    }

    private System.Security.Claims.ClaimsPrincipal Coordinator() =>
        CreateUser(sub: _data.Id("coordinator"), email: CoordinatorEmail, name: "Cora Coordinator");

    private (ResearchSubjectService Subjects, ScreeningNoteService Notes) CreateServices(
        Func<DelegatingHandler>? handler = null
    )
    {
        var access = new TrialAccessService(
            _db,
            new FakeStringLocalizer(),
            NullLogger<TrialAccessService>.Instance
        );
        var notes = new ScreeningNoteService(
            _db,
            access,
            new FakeStringLocalizer(),
            NullLogger<ScreeningNoteService>.Instance
        );
        var subjects = new ResearchSubjectService(
            fhir.CreateClientFactory(handler),
            access,
            notes,
            new FakeStringLocalizer(),
            _metrics.CreateRecruitMetrics(_db),
            NullLogger<ResearchSubjectService>.Instance
        );
        return (subjects, notes);
    }

    private async Task<(ResearchSubject Subject, TrialIdentifier Trial)> SeedSubjectAsync(
        TrialPermissionLevel coordinatorLevel = TrialPermissionLevel.Coordinator
    )
    {
        var study = _data.Study("SUBJECT");
        var patient = _data.Patient("Erika", "Mustermann");
        var subject = _data.Subject(study, patient);
        await _data.PutAsync(study, patient, subject);
        var trial = FhirTestData.TrialOf(study);
        await SeedGrantAsync(
            _db,
            trial,
            CoordinatorEmail,
            coordinatorLevel,
            _data.Id("coordinator")
        );
        return (subject, trial);
    }

    private async Task<ResearchSubject> ReadAsync(ResearchSubject subject) =>
        (await fhir.CreateClient().ReadAsync<ResearchSubject>($"ResearchSubject/{subject.Id}"))!;

    [Fact]
    public async Task A_coordinator_changes_a_subjects_status()
    {
        var (subject, trial) = await SeedSubjectAsync();
        var (subjects, _) = CreateServices();

        var lastUpdated = await subjects.UpdateStatusAsync(
            subject.Id!,
            "on-study",
            trial,
            Coordinator(),
            TestContext.Current.CancellationToken
        );

        var stored = await ReadAsync(subject);
        Assert.Equal(Status.OnStudy, stored.Status);
        Assert.Equal(stored.Meta?.LastUpdated, lastUpdated);
        Assert.Equal(
            "on-study",
            Assert.Single(_metrics.Of("recruit.screening.status_changes")).Tags["status"]
        );
    }

    [Fact]
    public async Task A_viewer_may_not_change_it()
    {
        var (subject, trial) = await SeedSubjectAsync(TrialPermissionLevel.Viewer);
        var (subjects, _) = CreateServices();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            subjects.UpdateStatusAsync(
                subject.Id!,
                "on-study",
                trial,
                Coordinator(),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(Status.Candidate, (await ReadAsync(subject)).Status);
        Assert.Empty(_metrics.Of("recruit.screening.status_changes"));
    }

    [Fact]
    public async Task A_change_someone_else_saved_in_the_meantime_is_not_overwritten()
    {
        var (subject, trial) = await SeedSubjectAsync();
        var (subjects, _) = CreateServices(() =>
            new WriteAfterFirstReadHandler(fhir, subject.Id!, Status.Ineligible)
        );

        var ex = await Assert.ThrowsAsync<FhirAccessException>(() =>
            subjects.UpdateStatusAsync(
                subject.Id!,
                "on-study",
                trial,
                Coordinator(),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("App.Errors.PatientRecordStatusConflict", ex.Message);
        Assert.Equal(Status.Ineligible, (await ReadAsync(subject)).Status);
    }

    [Fact]
    public async Task The_timeline_merges_status_changes_and_note_versions_newest_first()
    {
        var (subject, trial) = await SeedSubjectAsync();
        var (subjects, notes) = CreateServices();
        var identifier = subject.GetResearchSubjectIdentifierToken()!;
        var ct = TestContext.Current.CancellationToken;

        await subjects.UpdateStatusAsync(subject.Id!, "screening", trial, Coordinator(), ct);
        await notes.AddNoteAsync(identifier, "<p>Called the patient</p>", trial, Coordinator(), ct);
        var note = Assert.Single(await notes.GetNotesAsync(identifier, ct));
        await notes.UpdateNoteAsync(
            note.Id,
            "<p>Called the patient twice</p>",
            trial,
            Coordinator(),
            ct
        );
        // A version that leaves the status as it was isn't a status change.
        var unchanged = await ReadAsync(subject);
        unchanged.Period = new Period { Start = "2026-01-01" };
        await fhir.CreateClient().UpdateAsync(unchanged, versionAware: false, ct);
        await subjects.UpdateStatusAsync(subject.Id!, "eligible", trial, Coordinator(), ct);

        var timeline = await subjects.GetTimelineAsync(subject.Id!, identifier, ct);

        Assert.Equal(
            [
                (PatientHistoryEntryKind.StatusChange, (string?)null),
                (PatientHistoryEntryKind.Note, "<p>Called the patient twice</p>"),
                (PatientHistoryEntryKind.Note, "<p>Called the patient</p>"),
                (PatientHistoryEntryKind.StatusChange, null),
                (PatientHistoryEntryKind.StatusChange, null),
            ],
            timeline.Select(e => (e.Kind, e.Description))
        );
        Assert.Equal(timeline.OrderByDescending(e => e.Time), timeline);
        Assert.Equal(
            ["App.Timeline.StatusChanged", "App.Timeline.NoteEditedBy", "App.Timeline.NoteAddedBy"],
            timeline.Take(3).Select(e => e.Title)
        );
    }

    [Fact]
    public async Task Without_an_identifier_the_timeline_has_only_status_changes()
    {
        var (subject, _) = await SeedSubjectAsync();
        var (subjects, _) = CreateServices();

        var timeline = await subjects.GetTimelineAsync(
            subject.Id!,
            researchSubjectIdentifier: null,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PatientHistoryEntryKind.StatusChange, Assert.Single(timeline).Kind);
    }

    /// <summary>
    /// Saves a status change of its own right after the service under test has read the subject,
    /// as another user's concurrent edit would - so the service's version-aware write that follows
    /// is based on a version that's no longer current.
    /// </summary>
    private sealed class WriteAfterFirstReadHandler(
        FhirServerFixture fhir,
        string subjectId,
        Status concurrentStatus
    ) : DelegatingHandler
    {
        private bool _written;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (
                !_written
                && request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.EndsWith($"/ResearchSubject/{subjectId}")
            )
            {
                _written = true;
                var client = fhir.CreateClient();
                var current = await client.ReadAsync<ResearchSubject>(
                    $"ResearchSubject/{subjectId}",
                    ct: cancellationToken
                );
                current!.Status = concurrentStatus;
                await client.UpdateAsync(current, versionAware: false, cancellationToken);
            }

            return response;
        }
    }
}
