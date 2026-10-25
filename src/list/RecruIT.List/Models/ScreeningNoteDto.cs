namespace RecruIT.List.Models;

/// <summary>
/// One screening note for a ResearchSubject, stored in the screening_notes Postgres table (see
/// ScreeningNoteService). Multiple of these can exist per subject - each save inserts a new row,
/// which only its author can edit afterwards.
/// </summary>
public sealed class ScreeningNoteDto
{
    public Guid Id { get; init; }

    /// <summary>The current version's text.</summary>
    public required string Text { get; init; }

    public string? Author { get; init; }

    /// <summary>OIDC "sub" of the author - compared against the current user to decide whether they may edit it.</summary>
    public string? AuthorSubjectId { get; init; }

    public DateTimeOffset? Time { get; init; }

    /// <summary>When the note was last edited, null if never.</summary>
    public DateTimeOffset? EditedAt { get; init; }

    /// <summary>Superseded versions of Text, oldest first. Empty unless the note was edited.</summary>
    public IReadOnlyList<ScreeningNoteRevisionDto> PreviousVersions { get; init; } = [];
}

public sealed record ScreeningNoteRevisionDto(string Text, DateTimeOffset WrittenAt);
