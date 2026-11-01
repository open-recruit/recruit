using System.Collections;
using System.Globalization;
using System.Resources;
using BlazorBlueprint.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using RecruIT.List.Resources;
using RecruIT.List.Services.Localization;

namespace RecruIT.List.Tests;

/// <summary>
/// The real SharedResources.resx/.de.resx - unlike FakeStringLocalizer, which the access-logic
/// tests use to stay independent of translations.
/// </summary>
public sealed class LocalizationTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    private static readonly ResourceManager Resources = new(
        typeof(SharedResources).FullName!,
        typeof(SharedResources).Assembly
    );

    public static TheoryData<string> GenderKeys =>
        new(new[] { "male", "female", "other", "unknown" }.Select(GenderDisplay.GetKey));

    public static TheoryData<string> StatusKeys =>
        new(
            new[]
            {
                "candidate",
                "screening",
                "eligible",
                "ineligible",
                "on-study",
                "withdrawn",
            }.Select(StatusDisplay.GetKey)
        );

    [Theory]
    [MemberData(nameof(GenderKeys))]
    [MemberData(nameof(StatusKeys))]
    public void DisplayKeys_AreTranslatedInEveryCulture(string key)
    {
        Assert.False(string.IsNullOrEmpty(ResourceSetOf(English).GetString(key)), $"en: {key}");
        Assert.False(string.IsNullOrEmpty(ResourceSetOf(German).GetString(key)), $"de: {key}");
    }

    [Fact]
    public void DisplayKeys_PassUnknownLiteralsThrough()
    {
        Assert.Equal("nonbinary", GenderDisplay.GetKey("nonbinary"));
        Assert.Equal("pending-on-study", StatusDisplay.GetKey("pending-on-study"));
    }

    /// <summary>
    /// English is the neutral resx, so a key missing there shows up raw in the English UI, and one
    /// missing from the German file silently falls back to English.
    /// </summary>
    [Fact]
    public void EnglishAndGerman_DefineTheSameAppKeys()
    {
        var english = AppKeys(ResourceSetOf(English));
        var german = AppKeys(ResourceSetOf(German));

        Assert.Empty(english.Except(german));
        Assert.Empty(german.Except(english));
    }

    [Fact]
    public void BbLocalizer_UsesOurTranslationsAndFallsBackToTheLibraryDefaults()
    {
        var localizer = new AppBbLocalizer(
            new StringLocalizer<SharedResources>(
                new ResourceManagerStringLocalizerFactory(
                    Microsoft.Extensions.Options.Options.Create(new LocalizationOptions()),
                    NullLoggerFactory.Instance
                )
            )
        );
        var libraryDefault = new DefaultBbLocalizer()["Pagination.Next"];

        // German overrides the library's English strings...
        using (new UICulture(German))
        {
            var german = ResourceSetOf(German).GetString("Pagination.Next");
            Assert.False(string.IsNullOrEmpty(german));
            Assert.Equal(german, localizer["Pagination.Next"]);
        }

        // ...English has no entry of its own, so it gets the library's default.
        using (new UICulture(English))
        {
            Assert.Equal(libraryDefault, localizer["Pagination.Next"]);
        }
    }

    private static ResourceSet ResourceSetOf(CultureInfo culture) =>
        Resources.GetResourceSet(
            culture.Equals(English) ? CultureInfo.InvariantCulture : culture,
            createIfNotExists: true,
            tryParents: false
        ) ?? throw new InvalidOperationException($"No resources for {culture}.");

    private static HashSet<string> AppKeys(ResourceSet resources) =>
        resources
            .Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .Where(key => key.StartsWith("App.", StringComparison.Ordinal))
            .ToHashSet();

    private sealed class UICulture : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

        public UICulture(CultureInfo culture) => CultureInfo.CurrentUICulture = culture;

        public void Dispose() => CultureInfo.CurrentUICulture = _previous;
    }
}
