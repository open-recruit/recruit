using RecruIT.List.Models;

namespace RecruIT.List.Services.Notify;

/// <summary>
/// Pure scheduling logic, deliberately free of any FHIR/DB/clock dependency so it's trivially
/// unit-testable: given a subscriber's frequency preference and the instant an event occurred,
/// when should their next digest email go out? Several events landing in the same Daily/Weekly/
/// Monthly slot compute the same ScheduledFor instant, which is what lets
/// NotificationSenderService's "group due deliveries by subscription" batch them into one digest
/// email without any extra bookkeeping.
/// </summary>
public static class NotificationScheduling
{
    /// <summary>
    /// Fixed local send time for Monthly digests, which (unlike Daily/Weekly) have no
    /// user-configurable time - see NotificationSubscribeButton.razor's Monthly note.
    /// </summary>
    public static readonly TimeOnly MonthlyDigestTime = new(8, 0);

    /// <summary>
    /// Instant emails go out immediately (ScheduledFor = the event's own OccurredAt); everything
    /// else is scheduled for the subscriber's first digest slot strictly after the event, evaluated
    /// in their own time zone (timeZoneId, an IANA id captured client-side at subscribe time - see
    /// NotificationSubscribeButton.razor).
    /// </summary>
    public static DateTimeOffset ComputeEmailScheduledFor(
        NotificationFrequency frequency,
        DayOfWeek? dayOfWeek,
        TimeOnly? timeOfDay,
        string timeZoneId,
        DateTimeOffset occurredAt
    ) =>
        ComputeEmailScheduledFor(
            DigestSchedule.From(frequency, dayOfWeek, timeOfDay),
            timeZoneId,
            occurredAt
        );

    /// <inheritdoc cref="ComputeEmailScheduledFor(NotificationFrequency, DayOfWeek?, TimeOnly?, string, DateTimeOffset)"/>
    public static DateTimeOffset ComputeEmailScheduledFor(
        DigestSchedule schedule,
        string timeZoneId,
        DateTimeOffset occurredAt
    ) =>
        schedule switch
        {
            DigestSchedule.Instant => occurredAt,
            DigestSchedule.Daily(var timeOfDay) => FirstSlotAfter(
                occurredAt,
                timeZoneId,
                searchFrom => searchFrom + timeOfDay.ToTimeSpan(),
                slot => slot.AddDays(1)
            ),
            DigestSchedule.Weekly(var dayOfWeek, var timeOfDay) => FirstSlotAfter(
                occurredAt,
                timeZoneId,
                searchFrom => FirstOnOrAfter(searchFrom, dayOfWeek) + timeOfDay.ToTimeSpan(),
                slot => slot.AddDays(7)
            ),
            DigestSchedule.Monthly => FirstSlotAfter(
                occurredAt,
                timeZoneId,
                searchFrom =>
                    new DateTime(searchFrom.Year, searchFrom.Month, 1)
                    + MonthlyDigestTime.ToTimeSpan(),
                slot => slot.AddMonths(1)
            ),
        };

    /// <summary>
    /// Walks a digest's local wall-clock slots - the first one on or after searchFrom, then each
    /// next one - until one falls strictly after occurredAt.
    /// </summary>
    private static DateTimeOffset FirstSlotAfter(
        DateTimeOffset occurredAt,
        string timeZoneId,
        Func<DateTime, DateTime> firstSlotFrom,
        Func<DateTime, DateTime> nextSlot
    )
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        // Start from the day before the event's local date: a slot's later pass through a "fall
        // back" overlap can still be ahead of an event whose local time already reads the next day.
        var searchFrom = TimeZoneInfo.ConvertTime(occurredAt, timeZone).Date.AddDays(-1);
        var slotLocal = firstSlotFrom(searchFrom);

        // Compare instants rather than wall-clock times: during a "fall back" overlap, a slot whose
        // wall-clock time has already passed once is still ahead, on its later pass.
        while (true)
        {
            var slot = ToLocalOffset(slotLocal, timeZone);
            if (slot > occurredAt)
            {
                return slot;
            }
            slotLocal = nextSlot(slotLocal);
        }
    }

    /// <summary>
    /// Resolves a candidate local wall-clock time to an instant, handling the two edge cases of an
    /// offset change: a "spring forward" gap (candidateLocal never actually occurs) and a "fall
    /// back" overlap (candidateLocal occurs twice, at two different offsets).
    /// </summary>
    private static DateTimeOffset ToLocalOffset(DateTime candidateLocal, TimeZoneInfo timeZone)
    {
        // Round a time inside a gap forward to the first instant the wall clock actually reaches
        // once the gap is over, rather than silently picking one side of it.
        var probe = candidateLocal;
        var occurrences = Occurrences(probe, timeZone);
        while (occurrences.Count == 0)
        {
            probe = probe.AddMinutes(1);
            occurrences = Occurrences(probe, timeZone);
        }

        // Deterministically take the later occurrence of a time inside an overlap (the smaller,
        // usually standard-time, offset), so every slot is one well-defined instant.
        return occurrences.Max();
    }

    /// <summary>
    /// The instants whose wall-clock time in timeZone is local: none inside a gap, two inside an
    /// overlap. Worked out from instant-to-local offsets rather than TimeZoneInfo.IsInvalidTime/
    /// IsAmbiguousTime, which miss offset changes .NET models as a change of base offset rather
    /// than of daylight time (e.g. America/Ojinaga's spring forward in 2014, on Linux).
    /// </summary>
    private static List<DateTimeOffset> Occurrences(DateTime local, TimeZoneInfo timeZone)
    {
        // Every UTC offset is within a day of zero, so these two bracket every instant that could
        // read as local - and the offsets in effect either side of any change in between.
        var localAsUtc = new DateTimeOffset(local, TimeSpan.Zero);
        return
        [
            .. new[] { localAsUtc.AddDays(-1), localAsUtc.AddDays(1) }
                .Select(timeZone.GetUtcOffset)
                .Distinct()
                .Select(offset => new DateTimeOffset(local, offset))
                .Where(instant => timeZone.GetUtcOffset(instant) == instant.Offset),
        ];
    }

    private static DateTime FirstOnOrAfter(DateTime date, DayOfWeek dayOfWeek) =>
        date.AddDays(((int)dayOfWeek - (int)date.DayOfWeek + 7) % 7);
}
