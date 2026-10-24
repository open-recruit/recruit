using Hl7.Fhir.Model;
using RecruIT.List.Models;
using RecruIT.List.Services.Fhir;

namespace RecruIT.List.Tests;

public sealed class LibrarySqlDialectsTests
{
    [Fact]
    public void GetSqlDialects_ReturnsTheDialectOfEachDialectSpecificContent()
    {
        var library = LibraryWith(
            "application/sql",
            "application/sql;dialect=trino",
            "application/sql;dialect=duckdb"
        );

        Assert.Equal(["trino", "duckdb"], library.GetSqlDialects());
    }

    [Fact]
    public void GetSqlDialects_IgnoresWhitespaceAndCaseLikeTheQueryModule()
    {
        var library = LibraryWith(
            "Application/SQL; dialect=DuckDB",
            "application/sql;dialect=duckdb"
        );

        Assert.Equal(["duckdb"], library.GetSqlDialects());
    }

    [Fact]
    public void GetSqlDialects_IsEmptyForDialectAgnosticOrOtherContent()
    {
        var library = LibraryWith(
            "application/sql",
            "text/plain",
            null,
            "application/sql;dialect="
        );

        Assert.Empty(library.GetSqlDialects());
    }

    [Theory]
    [InlineData("trino", "Trino")]
    [InlineData("duckdb", "DuckDB")]
    [InlineData("postgresql", "postgresql")]
    public void DisplayNameOfContentTypeParameter_NamesKnownDialectsAndKeepsOthers(
        string parameter,
        string expected
    ) => Assert.Equal(expected, SqlDialectExtensions.DisplayNameOfContentTypeParameter(parameter));

    private static Library LibraryWith(params string?[] contentTypes)
    {
        var library = new Library();
        foreach (var contentType in contentTypes)
        {
            library.Content.Add(new Attachment { ContentType = contentType, Data = [1] });
        }
        return library;
    }
}
