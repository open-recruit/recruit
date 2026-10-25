namespace RecruIT.List.Data.Entities;

/// <summary>
/// A superseded version of a ScreeningNote's text, inserted on each edit and never updated or
/// deleted - the note row itself always holds the current version.
/// </summary>
public sealed class ScreeningNoteRevision
{
    public Guid Id { get; set; }

    public Guid ScreeningNoteId { get; set; }

    public required string Text { get; set; }

    /// <summary>When this version was written - the note's CreatedAt, or the UpdatedAt of the edit that produced it.</summary>
    public DateTimeOffset WrittenAt { get; set; }
}
