namespace RecruIT.List.Models;

internal static class AgeCalculator
{
    public static int Calculate(DateTimeOffset birthDate) =>
        Calculate(birthDate, DateTimeOffset.UtcNow);

    /// <summary>
    /// Completed years between birthDate and today. Someone born on 29 February turns a year older
    /// on 1 March in non-leap years.
    /// </summary>
    public static int Calculate(DateTimeOffset birthDate, DateTimeOffset today)
    {
        var age = today.Year - birthDate.Year;
        if (birthDate.Date > today.AddYears(-age).Date)
        {
            age--;
        }

        return age;
    }
}
