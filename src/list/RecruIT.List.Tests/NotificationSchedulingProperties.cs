using System.Collections.Concurrent;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using RecruIT.List.Models;
using RecruIT.List.Services.Notify;

namespace RecruIT.List.Tests;

/// <summary>
/// Property-based counterpart to NotificationSchedulingTests: rather than hand-picked instants,
/// FsCheck generates digest preferences across every system time zone, with most cases clustered
/// around one of that zone's UTC offset changes (DST or otherwise) - where scheduling by wall-clock
/// time goes wrong, and where hand-picked examples rarely look.
/// </summary>
[Properties(Arbitrary = [typeof(DigestCaseArbitrary)], MaxTest = 1000)]
public sealed class NotificationSchedulingProperties
{
    [Property]
    public void Instant_IsTheEventItself(DigestCase digest)
    {
        var instant = digest with { Frequency = NotificationFrequency.Instant };

        Assert.Equal(digest.OccurredAt, instant.ScheduledFor());
    }

    [Property]
    public void IsStrictlyAfterTheEvent(DigestCase digest) =>
        Assert.True(digest.ScheduledFor() > digest.OccurredAt);

    [Property]
    public void IsAtMostOnePeriodAway(DigestCase digest)
    {
        var period = digest.Frequency switch
        {
            NotificationFrequency.Daily => TimeSpan.FromDays(1),
            NotificationFrequency.Weekly => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(31),
        };

        // Slack for the slot's UTC offset differing from the event's.
        Assert.True(digest.ScheduledFor() - digest.OccurredAt <= period + TimeSpan.FromHours(3));
    }

    /// <summary>
    /// Checked against the zone's offset changes as worked out by OffsetChanges, rather than
    /// TimeZoneInfo.IsInvalidTime/IsAmbiguousTime, which miss some (see NotificationScheduling).
    /// </summary>
    [Property]
    public void LandsOnTheConfiguredSlot(DigestCase digest)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(digest.TimeZoneId);
        var scheduledFor = digest.ScheduledFor();
        var local = TimeZoneInfo.ConvertTime(scheduledFor, timeZone).DateTime;
        var slotTime = digest.TimeOfDay ?? NotificationScheduling.MonthlyDigestTime;
        var changes = OffsetChangesAround(digest.TimeZoneId, local.Year);

        if (TimeOnly.FromDateTime(local) == slotTime)
        {
            Assert.True(digest.IsSlotDay(local), $"{local:yyyy-MM-dd dddd} isn't a slot day");

            // A slot whose wall-clock time occurs twice goes out on its later pass.
            var overlap = changes.FirstOrDefault(change => change.Overlaps(local));
            Assert.True(
                overlap is null || scheduledFor >= overlap.At,
                $"{local:s} goes out on its earlier pass, before {overlap?.At:o}"
            );
            return;
        }

        // Otherwise the slot fell into a "spring forward" gap, and moves to the end of that gap.
        Assert.Contains(
            changes,
            change =>
                change.At == scheduledFor
                && new[] { local.Date, local.Date.AddDays(-1) }
                    .Select(date => date + slotTime.ToTimeSpan())
                    .Any(slot => digest.IsSlotDay(slot) && change.Skips(slot))
        );
    }

    /// <summary>
    /// The batching NotificationSenderService relies on: every event between this one and its
    /// digest slot lands in that same slot. Also rules out skipping a slot, or going backwards.
    /// </summary>
    [Property]
    public void EveryLaterEventUntilTheSlotSharesIt(DigestCase digest)
    {
        var scheduledFor = digest.ScheduledFor();

        foreach (var probe in Probes(digest.OccurredAt, scheduledFor))
        {
            var probeScheduledFor = digest.ScheduledFor(probe);
            Assert.True(
                probeScheduledFor == scheduledFor,
                $"An event at {probe:o} is scheduled for {probeScheduledFor:o}, not {scheduledFor:o}"
            );
        }
    }

    /// <summary>Instants in (from, to), densest near both ends, where an off-by-one-slot shows.</summary>
    private static IEnumerable<DateTimeOffset> Probes(DateTimeOffset from, DateTimeOffset to)
    {
        yield return to.AddTicks(-1);
        for (var step = TimeSpan.FromMinutes(1); step < to - from; step *= 2)
        {
            yield return from + step;
            yield return to - step;
        }
    }

    private static readonly ConcurrentDictionary<(string, int), OffsetChange[]> OffsetChangeCache =
        new();

    private static IEnumerable<OffsetChange> OffsetChangesAround(string timeZoneId, int year) =>
        Enumerable.Range(year - 1, 3).SelectMany(y => OffsetChanges(timeZoneId, y));

    /// <summary>Every instant in the year the zone's UTC offset changes, to the minute.</summary>
    private static OffsetChange[] OffsetChanges(string timeZoneId, int year) =>
        OffsetChangeCache.GetOrAdd(
            (timeZoneId, year),
            _ =>
            {
                var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                var changes = new List<OffsetChange>();
                for (
                    var day = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    day.Year == year;
                    day = day.AddDays(1)
                )
                {
                    var before = timeZone.GetUtcOffset(day);
                    if (timeZone.GetUtcOffset(day.AddDays(1)) == before)
                    {
                        continue;
                    }

                    // Binary search for the first minute of the day with the new offset.
                    var (unchanged, changed) = (0, 24 * 60);
                    while (changed - unchanged > 1)
                    {
                        var mid = (unchanged + changed) / 2;
                        if (timeZone.GetUtcOffset(day.AddMinutes(mid)) == before)
                        {
                            unchanged = mid;
                        }
                        else
                        {
                            changed = mid;
                        }
                    }
                    var at = day.AddMinutes(changed);
                    changes.Add(new OffsetChange(at, before, timeZone.GetUtcOffset(at)));
                }
                return [.. changes];
            }
        );

    private sealed record OffsetChange(DateTimeOffset At, TimeSpan Before, TimeSpan After)
    {
        private DateTime LocalBefore => At.UtcDateTime + Before;

        private DateTime LocalAfter => At.UtcDateTime + After;

        /// <summary>Whether the wall clock jumps forward over local here, so it never reads local.</summary>
        public bool Skips(DateTime local) => LocalBefore <= local && local < LocalAfter;

        /// <summary>Whether the wall clock jumps back over local here, so it reads local twice.</summary>
        public bool Overlaps(DateTime local) => LocalAfter <= local && local < LocalBefore;
    }

    public sealed record DigestCase(
        NotificationFrequency Frequency,
        DayOfWeek? DayOfWeek,
        TimeOnly? TimeOfDay,
        string TimeZoneId,
        DateTimeOffset OccurredAt
    )
    {
        public DateTimeOffset ScheduledFor() => ScheduledFor(OccurredAt);

        public DateTimeOffset ScheduledFor(DateTimeOffset occurredAt) =>
            NotificationScheduling.ComputeEmailScheduledFor(
                Frequency,
                DayOfWeek,
                TimeOfDay,
                TimeZoneId,
                occurredAt
            );

        public bool IsSlotDay(DateTime local) =>
            Frequency switch
            {
                NotificationFrequency.Weekly => local.DayOfWeek == DayOfWeek,
                NotificationFrequency.Monthly => local.Day == 1,
                _ => true,
            };
    }

    public static class DigestCaseArbitrary
    {
        private const int FirstYear = 2000;
        private const int LastYear = 2039;

        private static readonly string[] TimeZoneIds =
        [
            .. TimeZoneInfo.GetSystemTimeZones().Select(timeZone => timeZone.Id),
        ];

        public static Arbitrary<DigestCase> DigestCase() =>
            Arb.From(
                from timeZoneId in Gen.Elements(TimeZoneIds)
                from year in Gen.Choose(FirstYear, LastYear)
                from moment in MomentIn(timeZoneId, year)
                from frequency in Gen.Elements(
                    NotificationFrequency.Daily,
                    NotificationFrequency.Weekly,
                    NotificationFrequency.Monthly
                )
                select new DigestCase(
                    frequency,
                    frequency == NotificationFrequency.Weekly ? moment.SlotDay : null,
                    frequency == NotificationFrequency.Monthly ? null : moment.SlotTime,
                    timeZoneId,
                    moment.OccurredAt
                )
            );

        /// <summary>
        /// An event and a slot anywhere in the year - or, mostly, both near one of the zone's
        /// offset changes that year, the slot's wall-clock time inside (or just outside) the gap
        /// or overlap that change makes.
        /// </summary>
        private static Gen<Moment> MomentIn(string timeZoneId, int year)
        {
            var yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var anywhere =
                from second in Gen.Choose(
                    0,
                    (int)(yearStart.AddYears(1) - yearStart).TotalSeconds - 1
                )
                from minuteOfDay in Gen.Choose(0, 24 * 60 - 1)
                from slotDay in Gen.Elements(Enum.GetValues<DayOfWeek>())
                select new Moment(
                    yearStart.AddSeconds(second),
                    TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minuteOfDay)),
                    slotDay
                );

            var changes = OffsetChanges(timeZoneId, year);
            if (changes.Length == 0)
            {
                return anywhere;
            }

            var nearAnOffsetChange =
                from change in Gen.Elements(changes)
                from eventShiftSeconds in Gen.Choose(-3 * 60 * 60, 3 * 60 * 60)
                from offset in Gen.Elements(change.Before, change.After)
                from slotShiftMinutes in Gen.Choose(-90, 90)
                let slotLocal = change.At.UtcDateTime
                    + offset
                    + TimeSpan.FromMinutes(slotShiftMinutes)
                select new Moment(
                    change.At.AddSeconds(eventShiftSeconds),
                    TimeOnly.FromDateTime(slotLocal),
                    slotLocal.DayOfWeek
                );

            return Gen.Frequency((1, anywhere), (3, nearAnOffsetChange));
        }

        private sealed record Moment(
            DateTimeOffset OccurredAt,
            TimeOnly SlotTime,
            DayOfWeek SlotDay
        );
    }
}
