using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace RecruIT.List.E2ETests;

[Collection(AppCollection.Name)]
public sealed class MainPageTests(AppFixture app) : PageTest
{
    [Fact]
    public async Task MainPage_loads_and_renders_the_app_shell()
    {
        var response = await Page.GotoAsync(app.BaseUrl);

        Assert.NotNull(response);
        Assert.True(response.Ok, $"Expected a successful response, got {response.Status}.");

        // "recruIT" is the app's own brand text in the sidebar header, rendered unconditionally
        // regardless of whether FHIR/trial data loaded successfully - proof the app shell itself
        // came up, not just that some page returned 200.
        await Expect(Page.GetByText("recruIT", new PageGetByTextOptions { Exact = true }))
            .ToBeVisibleAsync();
    }

    [Fact]
    public async Task MainPage_shows_the_admin_nav_to_an_admin()
    {
        await Page.GotoAsync(app.BaseUrl);

        // Auth:Enabled=false signs every visitor in as an admin (DevBypassAuthenticationHandler),
        // so the sidebar group gated by the "Admin" policy must be there.
        await Expect(Page.GetByText("Administration", new PageGetByTextOptions { Exact = true }))
            .ToBeVisibleAsync();
    }
}
