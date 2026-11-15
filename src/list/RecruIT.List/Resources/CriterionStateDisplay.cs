using RecruIT.List.Models;

namespace RecruIT.List.Resources;

/// <summary>
/// The four ways a criterion can stand for a patient (see CriterionStatusDto), in the order the
/// screening list summarizes them: what rules a patient out first, then what still needs a look,
/// then what's settled.
/// </summary>
public enum CriterionState
{
    NotMet,
    Indeterminate,
    Unknown,
    Met,
}

/// <summary>How each CriterionState is labelled, drawn and counted (see CriterionStatusIcon, CriteriaSummary).</summary>
public static class CriterionStateDisplay
{
    public static CriterionState Of(bool? met, bool indeterminate) =>
        (met, indeterminate) switch
        {
            (true, _) => CriterionState.Met,
            (false, _) => CriterionState.NotMet,
            (null, true) => CriterionState.Indeterminate,
            (null, false) => CriterionState.Unknown,
        };

    public static CriterionState StateOf(this CriterionStatusDto criterion) =>
        Of(criterion.Met, criterion.Indeterminate);

    /// <summary>The state on its own, e.g. "Met".</summary>
    public static string LabelKey(CriterionState state) =>
        state switch
        {
            CriterionState.Met => "App.Patient.CriterionMet",
            CriterionState.NotMet => "App.Patient.CriterionNotMet",
            CriterionState.Indeterminate => "App.Patient.CriterionIndeterminate",
            _ => "App.Patient.CriterionUnknown",
        };

    /// <summary>A count of criteria in the state, e.g. "{0} met".</summary>
    public static string CountKey(CriterionState state) =>
        state switch
        {
            CriterionState.Met => "App.Criteria.CountMet",
            CriterionState.NotMet => "App.Criteria.CountNotMet",
            CriterionState.Indeterminate => "App.Criteria.CountIndeterminate",
            _ => "App.Criteria.CountUnknown",
        };

    public static string IconName(CriterionState state) =>
        state switch
        {
            CriterionState.Met => "circle-check",
            CriterionState.NotMet => "circle-x",
            CriterionState.Indeterminate => "circle-alert",
            _ => "circle-question-mark",
        };

    // Lucide icons render with stroke="currentColor", so coloring only needs a CSS `color` on a
    // wrapping element (see app.css) rather than a Class param on LucideIcon itself.
    public static string ColorClass(CriterionState state) =>
        state switch
        {
            CriterionState.Met => "criterion-icon-met",
            CriterionState.NotMet => "criterion-icon-not-met",
            CriterionState.Indeterminate => "criterion-icon-indeterminate",
            _ => "criterion-icon-unknown",
        };

    /// <summary>How many of <paramref name="criteria"/> are in each state, states with none left out, in CriterionState order.</summary>
    public static IReadOnlyList<(CriterionState State, int Count)> CountByState(
        IEnumerable<CriterionStatusDto> criteria
    ) => [.. criteria.GroupBy(StateOf).OrderBy(g => g.Key).Select(g => (g.Key, g.Count()))];
}
