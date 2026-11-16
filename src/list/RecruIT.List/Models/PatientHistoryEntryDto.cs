namespace RecruIT.List.Models;

/// <summary>
/// One entry in a patient's combined status-change + note timeline, newest first. Closed, so a
/// switch over the kinds of entry is exhaustive - and only a Note carries text, which is
/// (sanitized) HTML that gets rendered as markup.
/// </summary>
public closed record PatientHistoryEntryDto(DateTimeOffset Time, string Title)
{
    public sealed record StatusChange(DateTimeOffset Time, string Title)
        : PatientHistoryEntryDto(Time, Title);

    /// <summary>One version of a screening note, Html being its text as it read at that point.</summary>
    public sealed record Note(DateTimeOffset Time, string Title, string Html)
        : PatientHistoryEntryDto(Time, Title);
}
