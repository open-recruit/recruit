using Hangfire;

namespace RecruIT.List.Services.Notify;

/// <summary>The recurring jobs behind notifications, registered at startup (see Program.cs).</summary>
public static class NotificationJobs
{
    public const string DetectorJobId = "detect-notification-events";

    /// <summary>How often due emails are sent. Not configurable, unlike the detector's schedule.</summary>
    private const string SenderSchedule = "*/5 * * * *";

    /// <summary>
    /// Registers (or updates) the detector and the email sender. Safe to run on every replica at
    /// every startup: Hangfire dedupes by job id.
    /// </summary>
    /// <param name="scanSchedule">
    /// When the detector scans the screening lists: a cron expression in UTC, with five fields or
    /// six if it starts with seconds - Notifications:ScanSchedule.
    /// </param>
    /// <exception cref="InvalidOperationException">scanSchedule isn't a valid cron expression.</exception>
    public static void Register(IRecurringJobManager recurringJobManager, string scanSchedule)
    {
        try
        {
            recurringJobManager.AddOrUpdate<NotificationDetectorService>(
                DetectorJobId,
                s => s.PollAllTrialsAsync(CancellationToken.None),
                scanSchedule
            );
        }
        catch (ArgumentException ex) when (ex.ParamName == "cronExpression")
        {
            throw new InvalidOperationException(
                $"Notifications:ScanSchedule '{scanSchedule}' is not a valid cron expression, "
                    + "e.g. '* * * * *' for every minute or '*/5 * * * *' for every 5 minutes.",
                ex
            );
        }

        recurringJobManager.AddOrUpdate<NotificationSenderService>(
            NotificationSenderService.RecurringJobId,
            s => s.SendDueAsync(CancellationToken.None),
            SenderSchedule
        );

        // AddOrUpdate only ever adds/updates - it never removes a job id that's stopped being
        // registered, so the old "poll-screening-lists" recurring job (ScreeningListPollService, now
        // deleted) would otherwise sit in Hangfire's storage forever, still firing every minute and
        // failing to deserialize. Idempotent - a no-op once removed.
        recurringJobManager.RemoveIfExists("poll-screening-lists");
    }
}
