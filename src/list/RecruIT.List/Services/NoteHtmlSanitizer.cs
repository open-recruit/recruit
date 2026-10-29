using Ganss.Xss;

namespace RecruIT.List.Services;

/// <summary>
/// Single shared Ganss.Xss sanitizer config for screening-note HTML - used both when persisting a
/// note (ScreeningNoteService.AddNoteAsync, the actual storage invariant) and when rendering one
/// (PatientNotesSection/PatientHistorySection, defense-in-depth), so the two can't drift apart.
/// Tuned to what BbRichTextEditor writes rather than HtmlSanitizer's defaults, which would drop
/// checklist state and pasted images while letting a note restyle or overlay the page.
/// </summary>
public static class NoteHtmlSanitizer
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    public static string Sanitize(string html) => Sanitizer.Sanitize(html);

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();

        // Checklist items: <li data-list="checked|unchecked">.
        sanitizer.AllowedAttributes.Add("data-list");

        // Only the inline styles the editor itself writes (text colour, highlight, alignment) -
        // not e.g. position or z-index, which would let one note cover the page for every viewer.
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedCssProperties.UnionWith(["color", "background-color", "text-align"]);

        // Dropped and pasted images are embedded as data:image URLs - keep those, but no image
        // fetched from elsewhere, which would tell that host who opened the patient and when. Any
        // other data: URL (e.g. a data:text/html link) stays blocked.
        sanitizer.AllowedSchemes.Add("data");
        sanitizer.FilterUrl += (_, e) =>
        {
            var isImage = e.Tag.TagName == "IMG";
            var isDataUrl = e.OriginalUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            if (
                isImage != isDataUrl
                || (
                    isDataUrl
                    && !e.OriginalUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                e.SanitizedUrl = null;
            }
        };

        return sanitizer;
    }
}
