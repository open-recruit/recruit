using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RecruIT.List.Data;
using RecruIT.List.Data.Entities;
using RecruIT.List.Models;
using RecruIT.List.Services.Access;
using RecruIT.List.Services.Notify;
using static RecruIT.List.Tests.TestUsers;

namespace RecruIT.List.Tests;

/// <summary>
/// Subscribing, and the in-app feed - against Postgres, since the feed orders by DateTimeOffset in
/// the database, which SQLite can't translate (see NotificationInboxTests for the read/unread
/// mutations).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class NotificationSubscriptionServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly TrialIdentifier TrialA = new(FhirTestData.TrialIdentifierSystem, "A");
    private static readonly TrialIdentifier TrialB = new(FhirTestData.TrialIdentifierSystem, "B");

    private static readonly ClaimsPrincipal Alice = CreateUser(
        sub: "alice-sub",
        email: "alice@example.com"
    );
    private static readonly ClaimsPrincipal Bob = CreateUser(
        sub: "bob-sub",
        email: "bob@example.com"
    );

    private static readonly DateTimeOffset Noon = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private IDbContextFactory<AppDbContext> _db = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        foreach (var trial in new[] { TrialA, TrialB })
        {
            await SeedGrantAsync(
                _db,
                trial,
                "alice@example.com",
                TrialPermissionLevel.Viewer,
                "alice-sub"
            );
            await SeedGrantAsync(
                _db,
                trial,
                "bob@example.com",
                TrialPermissionLevel.Viewer,
                "bob-sub"
            );
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private NotificationSubscriptionService CreateService() =>
        new(
            _db,
            new TrialAccessService(
                _db,
                new FakeStringLocalizer(),
                NullLogger<TrialAccessService>.Instance
            )
        );

    [Fact]
    public async Task Subscribing_again_updates_the_subscription_instead_of_adding_one()
    {
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        await service.SubscribeAsync(
            Alice,
            TrialA,
            NotificationFrequency.Instant,
            null,
            null,
            "UTC",
            ct
        );
        await service.SubscribeAsync(
            Alice,
            TrialA,
            NotificationFrequency.Weekly,
            DayOfWeek.Monday,
            new TimeOnly(8, 0),
            "Europe/Berlin",
            ct
        );

        await using var db = _db.CreateDbContext();
        var subscription = await db.NotificationSubscriptions.SingleAsync(ct);
        Assert.Equal(
            (TrialA.ToToken(), "alice-sub", "alice@example.com"),
            (subscription.TrialIdentifier, subscription.SubjectId, subscription.Email)
        );
        Assert.Equal(
            (
                NotificationFrequency.Weekly,
                (DayOfWeek?)DayOfWeek.Monday,
                (TimeOnly?)new TimeOnly(8, 0),
                "Europe/Berlin"
            ),
            (
                subscription.Frequency,
                subscription.DayOfWeek,
                subscription.TimeOfDay,
                subscription.TimeZoneId
            )
        );
        Assert.Equal(subscription.Id, (await service.GetAsync(Alice, TrialA, ct))?.Id);
        Assert.Null(await service.GetAsync(Alice, TrialB, ct));
    }

    [Fact]
    public async Task Unsubscribing_removes_only_the_callers_subscription_to_that_trial()
    {
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;
        await service.SubscribeAsync(
            Alice,
            TrialA,
            NotificationFrequency.Instant,
            null,
            null,
            "UTC",
            ct
        );
        await service.SubscribeAsync(
            Alice,
            TrialB,
            NotificationFrequency.Instant,
            null,
            null,
            "UTC",
            ct
        );
        await service.SubscribeAsync(
            Bob,
            TrialA,
            NotificationFrequency.Instant,
            null,
            null,
            "UTC",
            ct
        );

        await service.UnsubscribeAsync(Alice, TrialA, ct);

        Assert.Null(await service.GetAsync(Alice, TrialA, ct));
        Assert.NotNull(await service.GetAsync(Alice, TrialB, ct));
        Assert.NotNull(await service.GetAsync(Bob, TrialA, ct));
    }

    [Fact]
    public async Task Subscribing_needs_an_email_address_to_send_to() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService()
                .SubscribeAsync(
                    CreateUser(sub: "no-email-sub"),
                    TrialA,
                    NotificationFrequency.Instant,
                    null,
                    null,
                    "UTC",
                    TestContext.Current.CancellationToken
                )
        );

    [Fact]
    public async Task The_unread_feed_lists_the_callers_in_app_notifications_newest_first()
    {
        var older = await NotifyAsync(TrialA, "Older Patient", Noon.AddHours(-2), "alice-sub");
        var newer = await NotifyAsync(TrialB, "Newer Patient", Noon, "alice-sub");
        await NotifyAsync(TrialA, "Bobs Patient", Noon.AddHours(1), "bob-sub");
        await NotifyAsync(
            TrialA,
            "Emailed Patient",
            Noon.AddHours(1),
            "alice-sub",
            NotificationChannel.Email
        );

        var feed = await CreateService()
            .ListInAppFeedAsync(
                Alice,
                NotificationFeedFilter.Unread,
                TestContext.Current.CancellationToken
            );

        Assert.Equal([newer, older], feed.Select(i => i.DeliveryId));
        var item = feed[0];
        Assert.Equal(
            (TrialB.ToToken(), "Newer Patient", Noon, (DateTimeOffset?)null),
            (item.TrialIdentifier, item.PatientDisplayName, item.OccurredAt, item.ReadAt)
        );
    }

    [Fact]
    public async Task The_read_feed_lists_the_most_recently_read_first()
    {
        var readFirst = await NotifyAsync(
            TrialA,
            "Read First",
            Noon,
            "alice-sub",
            readAt: Noon.AddHours(1)
        );
        var readLast = await NotifyAsync(
            TrialA,
            "Read Last",
            Noon.AddHours(-3),
            "alice-sub",
            readAt: Noon.AddHours(2)
        );
        await NotifyAsync(TrialA, "Unread", Noon, "alice-sub");

        var feed = await CreateService()
            .ListInAppFeedAsync(
                Alice,
                NotificationFeedFilter.Read,
                TestContext.Current.CancellationToken
            );

        Assert.Equal([readLast, readFirst], feed.Select(i => i.DeliveryId));
    }

    [Fact]
    public async Task Notifications_for_a_trial_the_caller_lost_access_to_are_hidden()
    {
        var kept = await NotifyAsync(TrialA, "Still Visible", Noon, "alice-sub");
        await NotifyAsync(TrialB, "No Longer Visible", Noon, "alice-sub");
        await using (var db = _db.CreateDbContext())
        {
            await db
                .TrialAccessGrants.Where(g =>
                    g.SubjectId == "alice-sub" && g.TrialIdentifierValue == "B"
                )
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var feed = await CreateService()
            .ListInAppFeedAsync(
                Alice,
                NotificationFeedFilter.Unread,
                TestContext.Current.CancellationToken
            );

        Assert.Equal([kept], feed.Select(i => i.DeliveryId));
    }

    /// <summary>One notification event and a delivery of it to subjectId, returning the delivery's id.</summary>
    private async Task<Guid> NotifyAsync(
        TrialIdentifier trial,
        string patient,
        DateTimeOffset occurredAt,
        string subjectId,
        NotificationChannel channel = NotificationChannel.InApp,
        DateTimeOffset? readAt = null
    )
    {
        await using var db = _db.CreateDbContext();
        var notification = new NotificationEvent
        {
            Id = Guid.NewGuid(),
            TrialIdentifier = trial.ToToken(),
            PatientReference = $"ResearchSubject/{Guid.NewGuid():N}",
            PatientDisplayName = patient,
            OccurredAt = occurredAt,
            DedupeKey = Guid.NewGuid().ToString(),
        };
        var delivery = new NotificationDelivery
        {
            Id = Guid.NewGuid(),
            NotificationEventId = notification.Id,
            NotificationSubscriptionId = Guid.NewGuid(),
            SubjectId = subjectId,
            Email = $"{subjectId}@example.com",
            Channel = channel,
            ScheduledFor = occurredAt,
            ReadAt = readAt,
        };
        db.NotificationEvents.Add(notification);
        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return delivery.Id;
    }
}
