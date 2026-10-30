using RecruIT.List.Models;

namespace RecruIT.List.Tests;

public sealed class SlugTests
{
    [Theory]
    [InlineData("Patients 18 years or older", "patients-18-years-or-older")]
    [InlineData("  Age >= 18, BMI < 30  ", "age-18-bmi-30")]
    [InlineData("Ältere Patienten mit Übergewicht", "altere-patienten-mit-ubergewicht")]
    [InlineData("Multiple   spaces--and---dashes", "multiple-spaces-and-dashes")]
    public void Create_LowercasesAndHyphenatesFreeText(string value, string expected) =>
        Assert.Equal(expected, Slug.Create(value));

    [Theory]
    [InlineData("Patients 18 years or older")]
    [InlineData("HbA1c 6.5%")]
    [InlineData("already-a-slug")]
    public void Create_IsIdempotent(string value)
    {
        var slug = Slug.Create(value);

        Assert.Equal(slug, Slug.Create(slug));
    }
}
