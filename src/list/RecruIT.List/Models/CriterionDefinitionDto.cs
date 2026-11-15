namespace RecruIT.List.Models;

/// <summary>One eligibility criterion's definition (not a per-patient status) - Group.characteristic.</summary>
public sealed class CriterionDefinitionDto
{
    public required string DisplayText { get; init; }

    public bool Exclude { get; init; }

    public string? Sql { get; init; }

    /// <summary>
    /// The id of the Library holding the criterion's SQL - what a patient's eligibility Observation
    /// (CriterionStatusDto.LibraryId) and a funnel step (FunnelStepDto.LibraryId) point back to.
    /// Null when the characteristic doesn't reference a Library.
    /// </summary>
    public string? LibraryId { get; init; }
}
