namespace RecruIT.List.Data.Entities;

/// <summary>
/// One screening note added to a ResearchSubject. Never deleted; its author may edit Text, but every
/// superseded version is first copied to a ScreeningNoteRevision, preserving the audit trail the
/// old FHIR Annotation-extension design had without needing FHIR version history for it. Keyed by
/// business identifiers ("system|value", see RecruIT.List.Services.Fhir.FhirConstants.UrlResearchSubjectIdentifier /
/// TrialIdentifier), not FHIR logical ids.
/// </summary>
public sealed class ScreeningNote
{
    public Guid Id { get; set; }

    public required string ResearchSubjectIdentifier { get; set; }

    public required string TrialIdentifier { get; set; }

    /// <summary>The current version's text.</summary>
    public required string Text { get; set; }

    /// <summary>OIDC "sub" of the author, if known. Only this user may edit the note.</summary>
    public string? AuthorSubjectId { get; set; }

    /// <summary>Snapshot of the author's display name at write time.</summary>
    public required string AuthorDisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When Text was last edited, null if never.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    public List<ScreeningNoteRevision> Revisions { get; set; } = [];
}
