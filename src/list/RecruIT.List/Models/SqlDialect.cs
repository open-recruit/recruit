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

    /// <summary>
    /// Display name for a content type's dialect parameter (e.g. "duckdb" is "DuckDB") - the raw
    /// value for a dialect this app can't author criteria in, but another Library source (e.g.
    /// superset-library-sync) may still have written.
    /// </summary>
    public static string DisplayNameOfContentTypeParameter(string dialectParameter) =>
        Enum.GetValues<SqlDialect>()
            .Where(d => d.ContentTypeParameter() == dialectParameter)
            .Select(d => d.DisplayName())
            .FirstOrDefault(dialectParameter);

    public static string DisplayName(this SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Trino => "Trino",
            SqlDialect.DuckDb => "DuckDB",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
}
