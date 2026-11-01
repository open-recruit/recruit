using Microsoft.Extensions.Localization;
using RecruIT.List.Resources;

namespace RecruIT.List.Services;

/// <summary>
/// What a component does when something it called throws: leaves a trace in the log, and decides
/// what the user gets to read. The services already log the failures they translate into a
/// UserFacingException (and the access checks' UnauthorizedAccessException is an answer, not a
/// failure), so only anything else - which would otherwise vanish, or be shown as a driver's raw
/// message - is logged here.
/// </summary>
public sealed class ErrorReporter(
    IStringLocalizer<SharedResources> localizer,
    ILogger<ErrorReporter> logger
)
{
    /// <summary>
    /// Logs the failure and returns the text to show for it: the message of a user-facing failure,
    /// else <paramref name="fallback"/> (a generic message if none is given).
    /// </summary>
    /// <param name="action">What was being attempted, in the log's words: "save the note".</param>
    public string Report(Exception ex, string action, string? fallback = null)
    {
        if (IsUserFacing(ex))
        {
            return ex.Message;
        }

        Log(ex, action);
        return fallback ?? localizer["App.Errors.Unexpected"];
    }

    /// <summary>
    /// For a failure the component recovers from itself, e.g. a best-effort load that carries on
    /// without its result: logged as a warning, since the page works without it.
    /// </summary>
    public void Swallowed(Exception ex, string action)
    {
        if (!IsUserFacing(ex))
        {
            logger.LogWarning(ex, "Failed to {Action}; carrying on without it", action);
        }
    }

    private void Log(Exception ex, string action)
    {
        if (ex is OperationCanceledException)
        {
            logger.LogDebug(ex, "Cancelled while trying to {Action}", action);
            return;
        }

        logger.LogError(ex, "Failed to {Action}", action);
    }

    private static bool IsUserFacing(Exception ex) =>
        ex is UserFacingException or UnauthorizedAccessException;
}
