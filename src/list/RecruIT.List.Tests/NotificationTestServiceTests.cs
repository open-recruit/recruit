using Hangfire;
using Hangfire.Common;
using Hl7.Fhir.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RecruIT.List.Data.Entities;
using RecruIT.List.Models;
using RecruIT.List.Options;
using RecruIT.List.Services.Notify;
using static RecruIT.List.Tests.TestUsers;
using FhirList = Hl7.Fhir.Model.List;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Tests;

/// <summary>
/// The Notification Test page's re-recommend action, through the real detector: it edits the
/// trial's List on the FHIR server and must notify every subscriber exactly once.
/// </summary>
[Collection(FhirServerCollection.Name)]
public sealed class NotificationTestServiceTests(FhirServerFixture fhir) : IDisposable
{
    private readonly SqliteDbContextFactory _db = new();
    private readonly RecordedMetrics _metrics = new();
    private readonly FakeRecurringJobManager _jobs = new();
    private readonly FhirTestData _data = new(fhir.CreateClient());

    public void Dispose()
    {
        _metrics.Dispose();
        _db.Dispose();
    }

    private System.Security.Claims.ClaimsPrincipal Admin() =>
        CreateUser(sub: _data.Id("admin"), email: "admin@example.com", isAdmin: true);

    private NotificationTestService CreateService(bool enabled = true)
    {
        var clientFactory = fhir.CreateClientFactory();
        return new NotificationTestService(
            clientFactory,
            new NotificationDetectorService(
                clientFactory,
                _db,
                _metrics.CreateRecruitMetrics(_db),
                NullLogger<NotificationDetectorService>.Instance
            ),
            _db,
            _jobs,
            Microsoft.Extensions.Options.Options.Create(
                new NotificationTestingOptions { Enabled = enabled }
            ),
            new FakeStringLocalizer()
        );
    }

    private async Task<(
        ResearchStudy Study,
        FhirList List,
        ResearchSubject Subject
    )> SeedTrialAsync()
    {
        var study = _data.Study("RERECOMMEND");
        var patient = _data.Patient("Erika", "Mustermann");
        var subject = _data.Subject(study, patient);
        var list = _data.ScreeningList(study, [(subject, DateTimeOffset.UtcNow.AddDays(-7))]);
        await _data.PutAsync(study, patient, subject, list);
        return (study, list, subject);
    }

    private async Task<NotificationSubscription> SubscribeAsync(ResearchStudy study, string name)
    {
        var subscription = new NotificationSubscription
        {
            Id = Guid.NewGuid(),
            TrialIdentifier = FhirTestData.TrialOf(study).ToToken(),
            SubjectId = _data.Id(name),
            Email = $"{name}@example.com",
            Frequency = NotificationFrequency.Instant,
            TimeZoneId = "Europe/Berlin",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await using var db = _db.CreateDbContext();
        db.NotificationSubscriptions.Add(subscription);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return subscription;
    }

    [Fact]
    public async Task Re_recommending_a_patient_notifies_every_subscriber_once()
    {
        var (study, list, subject) = await SeedTrialAsync();
        var alice = await SubscribeAsync(study, "alice");
        var bob = await SubscribeAsync(study, "bob");
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(2, await service.CountSubscribersAsync(FhirTestData.TrialOf(study), ct));
        var created = await service.ReRecommendAsync(list.Id!, subject.Id!, Admin(), ct);

        Assert.Equal(1, created);
        await using var db = _db.CreateDbContext();
        var notification = await db.NotificationEvents.SingleAsync(ct);
        Assert.Equal($"ResearchSubject/{subject.Id}", notification.PatientReference);
        Assert.Equal("Erika Mustermann", notification.PatientDisplayName);
        var deliveries = await db.NotificationDeliveries.ToListAsync(ct);
        Assert.All(deliveries, d => Assert.Equal(notification.Id, d.NotificationEventId));
        Assert.Equal(
            new[] { alice.Id, bob.Id }.Order(),
            deliveries.Select(d => d.NotificationSubscriptionId).Distinct().Order()
        );
        Assert.Equal(
            ("RERECOMMEND", 1.0),
            (
                Assert.Single(_metrics.Of("recruit.notifications.recommendations")).Tags["trial"],
                _metrics.Of("recruit.notifications.recommendations")[0].Value
            )
        );
    }

    [Fact]
    public async Task The_patient_is_back_on_the_list_afterwards_recommended_now()
    {
        var (_, list, subject) = await SeedTrialAsync();
        var before = DateTimeOffset.UtcNow;

        await CreateService()
            .ReRecommendAsync(
                list.Id!,
                subject.Id!,
                Admin(),
                TestContext.Current.CancellationToken
            );

        var stored = await fhir.CreateClient()
            .ReadAsync<FhirList>($"List/{list.Id}", ct: TestContext.Current.CancellationToken);
        var entry = Assert.Single(stored!.Entry);
        Assert.Equal($"ResearchSubject/{subject.Id}", entry.Item.Reference);
        Assert.True(DateTimeOffset.Parse(entry.Date!) >= before.AddSeconds(-1));
    }

    [Fact]
    public async Task A_patient_who_is_not_on_the_list_cannot_be_re_recommended()
    {
        var (_, list, _) = await SeedTrialAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService()
                .ReRecommendAsync(
                    list.Id!,
                    _data.Id("not-on-the-list"),
                    Admin(),
                    TestContext.Current.CancellationToken
                )
        );

        Assert.Equal("App.NotificationTest.ReRecommendNotOnList", ex.Message);
    }

    [Theory]
    [InlineData(true, false)] // enabled, but not for non-admins
    [InlineData(false, true)] // admins, but only where enabled
    public async Task Only_admins_may_and_only_where_it_is_enabled(bool enabled, bool admin)
    {
        var (_, list, subject) = await SeedTrialAsync();
        var service = CreateService(enabled);
        var user = admin ? Admin() : CreateUser(sub: _data.Id("user"), email: "user@example.com");

        Assert.False(service.IsAvailable(user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ReRecommendAsync(
                list.Id!,
                subject.Id!,
                user,
                TestContext.Current.CancellationToken
            )
        );
        Assert.Throws<UnauthorizedAccessException>(() => service.SendDueEmailsNow(user));
        Assert.Empty(_jobs.Triggered);
    }

    [Fact]
    public void Sending_due_emails_now_triggers_the_senders_recurring_job()
    {
        CreateService().SendDueEmailsNow(Admin());

        Assert.Equal([NotificationSenderService.RecurringJobId], _jobs.Triggered);
    }

    private sealed class FakeRecurringJobManager : IRecurringJobManager
    {
        public List<string> Triggered { get; } = [];

        public void AddOrUpdate(
            string recurringJobId,
            Job job,
            string cronExpression,
            RecurringJobOptions options
        ) => throw new NotSupportedException();

        public void Trigger(string recurringJobId) => Triggered.Add(recurringJobId);

        public void RemoveIfExists(string recurringJobId) => throw new NotSupportedException();
    }
}
