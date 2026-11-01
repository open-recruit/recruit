using Hangfire;
using Hangfire.Storage;
using RecruIT.List.Options;
using RecruIT.List.Services.Notify;

namespace RecruIT.List.Tests;

public sealed class NotificationJobsTests
{
    [Fact]
    public void The_detector_runs_on_the_configured_schedule_and_the_sender_every_5_minutes()
    {
        var jobs = new RecordingRecurringJobManager();

        NotificationJobs.Register(jobs, "*/10 * * * *");

        Assert.Equal(
            new Dictionary<string, string>
            {
                [NotificationJobs.DetectorJobId] = "*/10 * * * *",
                [NotificationSenderService.RecurringJobId] = "*/5 * * * *",
            },
            jobs.Registered
        );
    }

    [Fact]
    public void The_jobs_of_a_removed_service_are_cleaned_up()
    {
        var jobs = new RecordingRecurringJobManager();

        NotificationJobs.Register(jobs, "* * * * *");

        Assert.Equal(["poll-screening-lists"], jobs.Removed);
    }

    [Fact]
    public void The_default_schedule_is_every_minute() =>
        Assert.Equal("* * * * *", new NotificationOptions().ScanSchedule);

    [Theory]
    [InlineData("* * * * *")]
    [InlineData("*/5 * * * *")]
    [InlineData("0 6-18 * * 1-5")]
    [InlineData("*/30 * * * * *")] // six fields: seconds first
    public void Hangfire_accepts_the_documented_forms(string schedule)
    {
        // Hangfire checks the expression before it touches its storage, which here is a stub that
        // refuses everything - so getting as far as the storage means the expression was accepted.
        var manager = new RecurringJobManager(new RefusingJobStorage());

        var ex = Assert.Throws<NotSupportedException>(() =>
            NotificationJobs.Register(manager, schedule)
        );

        Assert.Equal("storage reached", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("every minute")]
    [InlineData("60 * * * *")]
    [InlineData("* * * *")]
    public void An_invalid_schedule_fails_startup_naming_the_setting_and_the_value(string schedule)
    {
        var manager = new RecurringJobManager(new RefusingJobStorage());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            NotificationJobs.Register(manager, schedule)
        );

        Assert.Contains("Notifications:ScanSchedule", ex.Message);
        Assert.Contains($"'{schedule}'", ex.Message);
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    private sealed class RefusingJobStorage : JobStorage
    {
        public override IMonitoringApi GetMonitoringApi() =>
            throw new NotSupportedException("storage reached");

        public override IStorageConnection GetConnection() =>
            throw new NotSupportedException("storage reached");
    }
}
