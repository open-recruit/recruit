using RecruIT.List.Models;

namespace RecruIT.List.Tests;

public sealed class AgeCalculatorTests
{
    private static DateTimeOffset Date(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2026, 6, 14, 39)] // the day before the birthday
    [InlineData(2026, 6, 15, 40)] // on the birthday
    [InlineData(2026, 12, 31, 40)]
    public void Calculate_CountsOnlyCompletedYears(int year, int month, int day, int expected) =>
        Assert.Equal(expected, AgeCalculator.Calculate(Date(1986, 6, 15), Date(year, month, day)));

    [Theory]
    [InlineData(2027, 2, 28, 26)] // non-leap year: not yet on 28 February...
    [InlineData(2027, 3, 1, 27)] // ...but on 1 March
    [InlineData(2028, 2, 29, 28)] // leap year: on the day itself
    public void Calculate_LeapDayBirthdays(int year, int month, int day, int expected) =>
        Assert.Equal(expected, AgeCalculator.Calculate(Date(2000, 2, 29), Date(year, month, day)));

    [Fact]
    public void Calculate_IsZeroInTheFirstYear() =>
        Assert.Equal(0, AgeCalculator.Calculate(Date(2026, 1, 10), Date(2026, 11, 30)));
}
