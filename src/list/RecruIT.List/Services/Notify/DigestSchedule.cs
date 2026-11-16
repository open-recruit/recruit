using RecruIT.List.Models;

namespace RecruIT.List.Services.Notify;

/// <summary>
/// When a subscriber's emails go out, with exactly the settings each frequency needs - unlike the
/// stored NotificationSubscription columns, where DayOfWeek/TimeOfDay are nullable for every
/// frequency. Closed, so a switch over the cases is exhaustive without a default arm, and adding
/// a frequency flags every switch that doesn't handle it yet.
/// </summary>
public closed record DigestSchedule
{
    /// <summary>Every event emailed on its own, right away.</summary>
    public sealed record Instant : DigestSchedule;

    public sealed record Daily(TimeOnly TimeOfDay) : DigestSchedule;

    public sealed record Weekly(DayOfWeek DayOfWeek, TimeOnly TimeOfDay) : DigestSchedule;

    /// <summary>The 1st of every month, at NotificationScheduling.MonthlyDigestTime.</summary>
    public sealed record Monthly : DigestSchedule;

    /// <summary>
    /// The schedule a subscription's stored columns describe - the one place a missing setting is
    /// caught, e.g. a Daily subscription without a time of day.
    /// </summary>
    public static DigestSchedule From(
        NotificationFrequency frequency,
        DayOfWeek? dayOfWeek,
        TimeOnly? timeOfDay
    ) =>
        frequency switch
        {
            NotificationFrequency.Instant => new Instant(),
            NotificationFrequency.Daily => new Daily(
                timeOfDay
                    ?? throw new ArgumentException(
                        "timeOfDay is required for Daily frequency.",
                        nameof(timeOfDay)
                    )
            ),
            NotificationFrequency.Weekly => new Weekly(
                dayOfWeek
                    ?? throw new ArgumentException(
                        "dayOfWeek is required for Weekly frequency.",
                        nameof(dayOfWeek)
                    ),
                timeOfDay
                    ?? throw new ArgumentException(
                        "timeOfDay is required for Weekly frequency.",
                        nameof(timeOfDay)
                    )
            ),
            NotificationFrequency.Monthly => new Monthly(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(frequency),
                frequency,
                "Unhandled notification frequency."
            ),
        };
}
