using RecruIT.List.Data.Entities;
using RecruIT.List.Models;

namespace RecruIT.List.Tests;

public sealed class RecruitMetricsTests : IDisposable
{
    private readonly SqliteDbContextFactory _db = new();
    private readonly RecordedMetrics _recorded = new();

    public void Dispose()
    {
        _recorded.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Each_measurement_is_tagged_with_what_it_is_broken_down_by()
    {
        var metrics = _recorded.CreateRecruitMetrics(_db);

        metrics.RecordDetectionRun(TimeSpan.FromSeconds(2), succeeded: false);
        metrics.RecordDetectionFailure();
        metrics.RecordRecommendations(3, "AMICA");
        metrics.RecordEmailSend(TimeSpan.FromMilliseconds(250), succeeded: true);
        metrics.RecordEmailDeliveriesDropped(4);
        metrics.RecordStatusChange("on-study");

        Assert.Equal(
            (2.0, "failure"),
            Single("recruit.notifications.detection.duration", "outcome")
        );
        Assert.Equal(
            1,
            Assert.Single(_recorded.Of("recruit.notifications.detection.failures")).Value
        );
        Assert.Equal((3.0, "AMICA"), Single("recruit.notifications.recommendations", "trial"));
        Assert.Equal(
            (0.25, "success"),
            Single("recruit.notifications.email.send.duration", "outcome")
        );
        Assert.Equal(4, Assert.Single(_recorded.Of("recruit.notifications.email.dropped")).Value);
        Assert.Equal((1.0, "on-study"), Single("recruit.screening.status_changes", "status"));
    }

    [Fact]
    public async Task Pending_emails_are_counted_from_the_database_on_each_scrape()
    {
        _recorded.CreateRecruitMetrics(_db);
        await using (var db = _db.CreateDbContext())
        {
            db.NotificationDeliveries.AddRange(
                Delivery(NotificationChannel.Email, sentAt: null),
                Delivery(NotificationChannel.Email, sentAt: null),
                Delivery(NotificationChannel.Email, sentAt: DateTimeOffset.UtcNow),
                Delivery(NotificationChannel.InApp, sentAt: null)
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _recorded.Scrape();

        Assert.Equal(2, Assert.Single(_recorded.Of("recruit.notifications.email.pending")).Value);
    }

    [Fact]
    public void A_scrape_the_database_cannot_answer_reports_nothing_rather_than_failing()
    {
        _recorded.CreateRecruitMetrics(_db);
        _db.Dispose();

        _recorded.Scrape();

        Assert.Empty(_recorded.Of("recruit.notifications.email.pending"));
    }

    private (double Value, object? Tag) Single(string instrument, string tag)
    {
        var measurement = Assert.Single(_recorded.Of(instrument));
        return (measurement.Value, measurement.Tags[tag]);
    }

    private static NotificationDelivery Delivery(
        NotificationChannel channel,
        DateTimeOffset? sentAt
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            NotificationEventId = Guid.NewGuid(),
            NotificationSubscriptionId = Guid.NewGuid(),
            SubjectId = "alice-sub",
            Email = "alice@example.com",
            Channel = channel,
            ScheduledFor = DateTimeOffset.UtcNow,
            SentAt = sentAt,
        };
}
