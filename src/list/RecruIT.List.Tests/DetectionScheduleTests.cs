using RecruIT.List.Services.Notify;

namespace RecruIT.List.Tests;

public sealed class DetectionScheduleTests
{
    [Theory]
    [InlineData(60, "* * * * *", 1)]
    [InlineData(1, "* * * * *", 1)]
    [InlineData(0, "* * * * *", 1)]
    [InlineData(-30, "* * * * *", 1)]
    [InlineData(61, "*/2 * * * *", 2)]
    [InlineData(300, "*/5 * * * *", 5)]
    [InlineData(3540, "*/59 * * * *", 59)]
    [InlineData(3600, "0 */1 * * *", 60)]
    [InlineData(5400, "0 */2 * * *", 120)]
    [InlineData(86400, "0 */23 * * *", 1380)]
    [InlineData(int.MaxValue, "0 */23 * * *", 1380)]
    public void The_interval_is_rounded_up_to_whole_minutes_then_hours(
        int seconds,
        string expectedCron,
        int expectedMinutes
    )
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), DetectionSchedule.IntervalFor(seconds));
        Assert.Equal(expectedCron, DetectionSchedule.CronFor(seconds));
    }
}
