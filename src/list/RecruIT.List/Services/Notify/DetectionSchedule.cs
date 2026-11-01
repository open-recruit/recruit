using Hangfire;

namespace RecruIT.List.Services.Notify;

/// <summary>
/// Turns Notifications:ScanIntervalSeconds into the detector job's cron schedule. Hangfire
/// schedules recurring jobs by cron expression, which has no finer resolution than a minute - so
/// the interval is rounded up to whole minutes (or, from an hour on, whole hours).
/// </summary>
public static class DetectionSchedule
{
    private const int MaxHours = 23;

    /// <summary>How often the detector actually runs for the configured number of seconds.</summary>
    public static TimeSpan IntervalFor(int scanIntervalSeconds)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(scanIntervalSeconds / 60.0));
        return minutes < 60
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromHours(Math.Min(MaxHours, (int)Math.Ceiling(minutes / 60.0)));
    }

    public static string CronFor(int scanIntervalSeconds) =>
        IntervalFor(scanIntervalSeconds) switch
        {
            { TotalMinutes: 1 } => Cron.Minutely(),
            { TotalMinutes: < 60 } interval => Cron.MinuteInterval((int)interval.TotalMinutes),
            var interval => Cron.HourInterval((int)interval.TotalHours),
        };
}
