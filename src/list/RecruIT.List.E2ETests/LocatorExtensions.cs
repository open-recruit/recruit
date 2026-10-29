using Microsoft.Playwright;

namespace RecruIT.List.E2ETests;

internal static class LocatorExtensions
{
    /// <summary>
    /// Pages are prerendered, so a click that lands before Blazor's circuit has connected does
    /// nothing - clicks <paramref name="trigger"/> until <paramref name="opened"/> actually shows.
    /// </summary>
    public static async Task ClickUntilVisibleAsync(
        this ILocator trigger,
        ILocator opened,
        string failureMessage
    )
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await trigger.ClickAsync();
            if (await BecomesVisibleAsync(opened, timeoutMs: 1500))
            {
                return;
            }
        }

        throw new TimeoutException(failureMessage);
    }

    private static async Task<bool> BecomesVisibleAsync(ILocator locator, float timeoutMs)
    {
        try
        {
            await locator.WaitForAsync(new() { Timeout = timeoutMs });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
