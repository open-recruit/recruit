using Hl7.Fhir.Model;
using RecruIT.List.Models;
using RecruIT.List.Services.Fhir;

namespace RecruIT.List.Tests;

public sealed class SqlContentTypeTests
{
    [Theory]
    [InlineData(SqlDialect.Trino)]
    [InlineData(SqlDialect.DuckDb)]
    public void DialectOf_ReadsBackTheContentTypeWrittenForADialect(SqlDialect dialect) =>
        Assert.Equal(
            dialect.ContentTypeParameter(),
            SqlContentType.DialectOf(SqlContentType.For(dialect))
        );

    [Fact]
    public void DialectOf_IgnoresWhitespaceAndCaseLikeTheQueryModule() =>
        Assert.Equal("duckdb", SqlContentType.DialectOf("Application/SQL; dialect=DuckDB"));

    [Theory]
    [InlineData(SqlContentType.DialectAgnostic)]
    [InlineData("text/plain")]
    [InlineData("application/sql;dialect=")]
    [InlineData(null)]
    public void DialectOf_IsNullForDialectAgnosticOrOtherContent(string? contentType) =>
        Assert.Null(SqlContentType.DialectOf(contentType));

    [Theory]
    [InlineData("trino", "Trino")]
    [InlineData("duckdb", "DuckDB")]
    [InlineData("postgresql", "postgresql")]
    public void DialectDisplayName_NamesKnownDialectsAndKeepsOthers(
        string parameter,
        string expected
    ) => Assert.Equal(expected, SqlContentType.DialectDisplayName(parameter));

    [Fact]
    public void GetSqlDialects_ReturnsEachDialectOnce_InContentOrder()
    {
        var library = new Library();
        foreach (
            var contentType in new[]
            {
                SqlContentType.DialectAgnostic,
                "application/sql;dialect=trino",
                "application/sql;dialect=duckdb",
                "application/sql; dialect=Trino",
            }
        )
        {
            library.Content.Add(new Attachment { ContentType = contentType, Data = [1] });
        }

        Assert.Equal(["trino", "duckdb"], library.GetSqlDialects());
    }
}
