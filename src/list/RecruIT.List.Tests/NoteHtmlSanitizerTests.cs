using RecruIT.List.Services;

namespace RecruIT.List.Tests;

public sealed class NoteHtmlSanitizerTests
{
    [Theory]
    [InlineData("<p>Hello <strong>bold</strong> <em>it</em> <u>u</u> <s>s</s></p>")]
    [InlineData("<h2>Heading</h2><ol><li>one</li></ol><ul><li>two</li></ul>")]
    [InlineData(
        "<ul><li data-list=\"checked\">done</li><li data-list=\"unchecked\">todo</li></ul>"
    )]
    [InlineData("<p style=\"text-align: center\">centred</p>")]
    [InlineData("<a href=\"https://example.org/\">link</a>")]
    [InlineData("<img src=\"data:image/png;base64,iVBORw0KGgo=\">")]
    public void Sanitize_KeepsWhatTheEditorWrites(string html) =>
        Assert.Equal(html, NoteHtmlSanitizer.Sanitize(html));

    [Theory]
    [InlineData("<script>alert(1)</script><p>x</p>", "<p>x</p>")]
    [InlineData("<p onclick=\"steal()\">x</p>", "<p>x</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"data:text/html,x\">x</a>", "<a>x</a>")]
    [InlineData("<iframe src=\"https://example.org/\"></iframe>x", "x")]
    public void Sanitize_RemovesScriptsAndScriptableUrls(string html, string expected) =>
        Assert.Equal(expected, NoteHtmlSanitizer.Sanitize(html));

    [Fact]
    public void Sanitize_DropsImagesFetchedFromElsewhere() =>
        Assert.Equal(
            "<img>",
            NoteHtmlSanitizer.Sanitize("<img src=\"https://tracker.example/pixel.gif\">")
        );

    [Fact]
    public void Sanitize_KeepsOnlyTheEditorsOwnInlineStyles() =>
        Assert.Equal(
            "<p style=\"text-align: center\">x</p>",
            NoteHtmlSanitizer.Sanitize(
                "<p style=\"text-align: center; position: fixed; inset: 0; z-index: 9999\">x</p>"
            )
        );

    [Theory]
    [InlineData("<p style=\"color: #dc2626\">x</p><script>x</script>")]
    [InlineData("<ul><li data-list=\"checked\" data-other=\"1\">x</li></ul>")]
    public void Sanitize_IsIdempotent(string html)
    {
        var once = NoteHtmlSanitizer.Sanitize(html);

        Assert.Equal(once, NoteHtmlSanitizer.Sanitize(once));
    }
}
