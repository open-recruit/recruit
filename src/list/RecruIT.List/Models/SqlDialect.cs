namespace RecruIT.List.Models;

/// <summary>
/// The SQL dialect a newly-authored criterion's SQL is written in - stored as the Library's
/// application/sql;dialect=&lt;dialect&gt; content, which query-sql-on-fhir runs directly when it
/// matches its configured query-sql-on-fhir.sql-dialect.
/// </summary>
public enum SqlDialect
{
    Trino = 0,
    DuckDb = 1,
}

public static class SqlDialectExtensions
{
    /// <summary>The dialect parameter value of the application/sql content type, e.g. "trino".</summary>
    public static string ContentTypeParameter(this SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Trino => "trino",
            SqlDialect.DuckDb => "duckdb",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };

    public static string DisplayName(this SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Trino => "Trino",
            SqlDialect.DuckDb => "DuckDB",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
}

/// <summary>
/// The content types of a criterion Library's SQL attachments, both ways - what this app writes
/// (see EligibilityCriteriaService) and how it reads them back (see FhirBundleHelpers.GetSqlDialects).
/// Every Library carries the SQL as dialect-agnostic content, and optionally once more per dialect
/// as application/sql;dialect=&lt;dialect&gt;, which query-sql-on-fhir runs directly when the
/// dialect is its configured one.
/// </summary>
public static class SqlContentType
{
    public const string DialectAgnostic = "application/sql";

    private const string DialectPrefix = $"{DialectAgnostic};dialect=";

    public static string For(SqlDialect dialect) => DialectPrefix + dialect.ContentTypeParameter();

    /// <summary>
    /// The dialect parameter of a dialect-specific SQL content type, e.g. "trino" - null for
    /// dialect-agnostic or non-SQL content. Normalized the way query-sql-on-fhir does before it
    /// compares a content type against its own dialect: whitespace removed, lowercased.
    /// </summary>
    public static string? DialectOf(string? contentType)
    {
        var normalized = string.Concat(
                (contentType ?? string.Empty).Where(c => !char.IsWhiteSpace(c))
            )
            .ToLowerInvariant();
        return
            normalized.StartsWith(DialectPrefix, StringComparison.Ordinal)
            && normalized.Length > DialectPrefix.Length
            ? normalized[DialectPrefix.Length..]
            : null;
    }

    /// <summary>
    /// Display name for a dialect parameter (e.g. "duckdb" is "DuckDB") - the raw value for a
    /// dialect this app can't author criteria in, but another Library source (e.g.
    /// superset-library-sync) may still have written.
    /// </summary>
    public static string DialectDisplayName(string dialectParameter) =>
        Enum.GetValues<SqlDialect>()
            .Where(d => d.ContentTypeParameter() == dialectParameter)
            .Select(d => d.DisplayName())
            .FirstOrDefault(dialectParameter);
}
