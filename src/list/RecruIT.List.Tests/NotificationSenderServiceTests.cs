using Hl7.Fhir.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RecruIT.List.Data;
using RecruIT.List.Data.Entities;
using RecruIT.List.Models;
using RecruIT.List.Options;
using RecruIT.List.Services.Access;
using RecruIT.List.Services.Fhir;
using RecruIT.List.Services.Notify;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Tests;

/// <summary>
/// The email sender's recurring run, against a real FHIR server for the trial lookup (its acronym
/// goes in the subject) and SQLite for the deliveries, with a channel that only records.
/// </summary>
[Collection(FhirServerCollection.Name)]
public sealed class NotificationSenderServiceTests(FhirServerFixture fhir, PostgresFixture postgres)
    : IAsyncLifetime
{
    // Postgres rather than SQLite: the run aggregates DateTimeOffsets in the database.
    private IDbContextFactory<AppDbContext> _db = null!;
    private readonly RecordedMetrics _metrics = new();
    private readonly FhirTestData _data = new(fhir.CreateClient());
    private readonly FakeNotificationChannel _channel = new();
    private readonly CapturingLogger<NotificationSenderService> _log = new();

    public async ValueTask InitializeAsync() =>
        _db = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _metrics.Dispose();
        return ValueTask.CompletedTask;
    }

    private NotificationSenderService CreateService(INotificationChannel? channel = null) =>
        new(
            _db,
            new ScreeningListService(
                fhir.CreateClientFactory(),
                new TrialAccessService(
                    _db,
                    new FakeStringLocalizer(),
                    NullLogger<TrialAccessService>.Instance,
                    AdminRoles.Default
                ),
                new FakeStringLocalizer(),
                NullLogger<ScreeningListService>.Instance
            ),
            new TrialAccessService(
                _db,
                new FakeStringLocalizer(),
                NullLogger<TrialAccessService>.Instance,
                AdminRoles.Default
            ),
            Microsoft.Extensions.Options.Options.Create(
                new EmailNotificationOptions
                {
                    PublicBaseUrl = "https://recruit.example.org/",
                    From = "recruit@example.org",
                    SubjectTemplate = "New patients for {0}",
                    SmtpHost = "unused",
                }
            ),
            // Authentication off: nobody needs a grant row to be mailed.
            Microsoft.Extensions.Options.Options.Create(new AuthOptions { Enabled = false }),
            channel ?? _channel,
            _metrics.CreateRecruitMetrics(_db),
            _log
        );

    /// <summary>A trial that exists on the FHIR server, so its acronym can be looked up.</summary>
    private async Task<string> SeedTrialAsync()
    {
        var study = _data.Study("SEND");
        await _data.PutAsync(study, _data.ScreeningList(study, []));
        return FhirTestData.TrialOf(study).ToToken();
    }

    /// <summary>One subscriber with one pending email delivery for a new event.</summary>
    private async Task<(Guid Subscription, Guid Delivery)> QueueAsync(
        string trial,
        string email,
        NotificationFrequency frequency = NotificationFrequency.Instant,
        string timeZone = "Europe/Berlin"
    )
    {
        var now = DateTimeOffset.UtcNow;
        var subscription = new NotificationSubscription
        {
            Id = Guid.NewGuid(),
            TrialIdentifier = trial,
            SubjectId = $"{email}-sub",
            Email = email,
            Frequency = frequency,
            TimeOfDay = frequency == NotificationFrequency.Instant ? null : new TimeOnly(9, 0),
            TimeZoneId = timeZone,
            CreatedAt = now.AddDays(-3),
            UpdatedAt = now.AddDays(-3),
        };
        var notification = new NotificationEvent
        {
            Id = Guid.NewGuid(),
            TrialIdentifier = trial,
            PatientReference = $"ResearchSubject/{Guid.NewGuid():N}",
            PatientDisplayName = "Erika Mustermann",
            OccurredAt = now.AddHours(-1),
            DedupeKey = Guid.NewGuid().ToString(),
        };
        var delivery = new NotificationDelivery
        {
            Id = Guid.NewGuid(),
            NotificationEventId = notification.Id,
            NotificationSubscriptionId = subscription.Id,
            SubjectId = subscription.SubjectId,
            Email = email,
            Channel = NotificationChannel.Email,
            ScheduledFor = notification.OccurredAt,
        };
        await using var db = _db.CreateDbContext();
        db.NotificationSubscriptions.Add(subscription);
        db.NotificationEvents.Add(notification);
        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (subscription.Id, delivery.Id);
    }

    private async Task<bool> IsSentAsync(Guid delivery)
    {
        await using var db = _db.CreateDbContext();
        return (
            await db
                .NotificationDeliveries.AsNoTracking()
                .SingleAsync(d => d.Id == delivery, TestContext.Current.CancellationToken)
        ).SentAt
            is not null;
    }

    [Fact]
    public async Task Instant_subscribers_are_each_sent_one_email_and_it_is_recorded()
    {
        var trial = await SeedTrialAsync();
        var (_, alice) = await QueueAsync(trial, "alice@example.org");
        var (_, bob) = await QueueAsync(trial, "bob@example.org");

        await CreateService().SendDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["alice@example.org", "bob@example.org"], _channel.SentTo.Order().ToArray());
        Assert.True(await IsSentAsync(alice));
        Assert.True(await IsSentAsync(bob));

        await CreateService().SendDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _channel.SentTo.Count); // nothing is sent twice
    }

    [Fact]
    public async Task A_cancelled_run_keeps_what_it_sent_so_the_next_run_does_not_send_it_again()
    {
        var trial = await SeedTrialAsync();
        var (_, first) = await QueueAsync(trial, "alice@example.org");
        var (_, second) = await QueueAsync(trial, "bob@example.org");
        using var shutdown = new CancellationTokenSource();
        _channel.OnSend = (_, number) =>
        {
            if (number == 2)
            {
                shutdown.Cancel();
                throw new OperationCanceledException(shutdown.Token);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService().SendDueAsync(shutdown.Token)
        );

        // Exactly one of the two went out, and that one was recorded.
        var sentOnce = Assert.Single(_channel.SentTo.Take(1));
        Assert.Equal(
            1,
            new[] { await IsSentAsync(first), await IsSentAsync(second) }.Count(sent => sent)
        );

        var alreadySent = _channel.SentTo.ToList();
        var next = new FakeNotificationChannel();
        await CreateService(next).SendDueAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(sentOnce, next.SentTo);
        Assert.Single(next.SentTo);
        Assert.True(await IsSentAsync(first));
        Assert.True(await IsSentAsync(second));
        Assert.Single(alreadySent.Take(1));
    }

    [Fact]
    public async Task A_failed_send_is_retried_later_without_holding_back_the_other_recipients()
    {
        var trial = await SeedTrialAsync();
        var (_, bad) = await QueueAsync(trial, "bad@example.org");
        var (_, good) = await QueueAsync(trial, "good@example.org");
        _channel.OnSend = (recipient, _) =>
        {
            if (recipient == "bad@example.org")
            {
                throw new InvalidOperationException("mailbox unavailable");
            }
        };

        await CreateService().SendDueAsync(TestContext.Current.CancellationToken);

        Assert.True(await IsSentAsync(good));
        Assert.False(await IsSentAsync(bad));
        Assert.Contains(
            _log.Entries,
            e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException
        );
    }

    [Fact]
    public async Task A_subscription_whose_schedule_cannot_be_worked_out_does_not_stop_the_others()
    {
        var trial = await SeedTrialAsync();
        var (_, broken) = await QueueAsync(
            trial,
            "broken@example.org",
            NotificationFrequency.Daily,
            timeZone: "Nowhere/Land"
        );
        var (_, fine) = await QueueAsync(trial, "fine@example.org");

        await CreateService().SendDueAsync(TestContext.Current.CancellationToken);

        Assert.True(await IsSentAsync(fine));
        Assert.False(await IsSentAsync(broken));
        var error = Assert.Single(_log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("Nowhere/Land", error.Message);
    }
}
