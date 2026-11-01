using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace RecruIT.List.E2ETests;

/// <summary>
/// Adding and editing a screening note from the patient dialog, both saved with Ctrl+Enter. Opens
/// the first patient on the Patients page rather than a seeded one by name, so it also works
/// against an E2E_BASE_URL instance; the note text is unique per run so earlier runs' notes don't
/// interfere.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class ScreeningNoteTests(AppFixture app) : PageTest
{
    public override BrowserNewContextOptions ContextOptions() => new() { Locale = "en-US" };

    [Fact]
    public async Task A_note_saved_with_ctrl_enter_can_be_edited_and_keeps_its_previous_version()
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var original = $"Original note {runId}";
        var edited = $"Edited note {runId}";

        await OpenFirstPatientAsync();

        var newNoteEditor = Page.Locator(".ql-editor[data-placeholder='Add a screening note...']");
        await newNoteEditor.ClickAsync();
        // Ctrl+Enter straight after typing, inside the editor's 150ms change debounce - the save
        // has to read the editor's content itself rather than the not-yet-updated bound value.
        await Page.Keyboard.TypeAsync(original);
        await Page.Keyboard.PressAsync("Control+Enter");

        await Expect(Page.GetByText(original, new() { Exact = true })).ToBeVisibleAsync();
        await Expect(newNoteEditor).ToHaveTextAsync("");
        await Expect(Page.GetByText("Ctrl+Enter to save")).ToBeVisibleAsync();

        // Notes are newest first, and only the current user's own notes offer an Edit button.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Edit note" }).First.ClickAsync();
        var editEditor = Page.Locator(".ql-editor").GetByText(original, new() { Exact = true });
        await editEditor.ClickAsync();
        await Page.Keyboard.PressAsync("ControlOrMeta+A");
        await Page.Keyboard.TypeAsync(edited);
        await Page.Keyboard.PressAsync("Control+Enter");

        await Expect(Page.GetByText(edited, new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByText(original, new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(Page.GetByText("edited", new() { Exact = false }).First).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Show 1 previous version(s)" })
            .First.ClickAsync();
        await Expect(Page.GetByText(original, new() { Exact = true })).ToBeVisibleAsync();
    }

    /// <summary>Opens the first patient's dialog from the Patients page, with its note editor.</summary>
    private async Task OpenFirstPatientAsync()
    {
        await Page.GotoAsync($"{app.BaseUrl}/patients");
        var studyChip = Page.Locator("tbody tr")
            .First.Locator("td")
            .Nth(2)
            .Locator("div > div")
            .First;

        await studyChip.ClickUntilVisibleAsync(
            Page.Locator(".ql-editor").First,
            "Clicking the first patient never opened the patient dialog."
        );
    }
}
