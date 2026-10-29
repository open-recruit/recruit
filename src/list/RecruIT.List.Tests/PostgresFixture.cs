using Microsoft.EntityFrameworkCore;
using Npgsql;
using RecruIT.List.Data;
using Testcontainers.PostgreSql;

namespace RecruIT.List.Tests;

/// <summary>
/// Shared by every test that needs real Postgres behavior SQLite doesn't reproduce (concurrency
/// tokens, unique violations, ordering by DateTimeOffset) - one container for all of them, but a
/// database of their own each (see CreateDatabaseAsync).
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(
        "docker.io/library/postgres:18.4@sha256:a02db8cac496f15b094798a38254f14d6e00741f709360e5e00bb6668ea31636"
    ).Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    /// <summary>A freshly migrated database no other test uses.</summary>
    public async Task<IDbContextFactory<AppDbContext>> CreateDatabaseAsync(
        CancellationToken ct = default
    )
    {
        var database = $"test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await connection.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
            await create.ExecuteNonQueryAsync(ct);
        }

        var factory = new DbContextFactory(
            new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
            {
                Database = database,
            }.ConnectionString
        );
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync(ct);
        return factory;
    }

    /// <summary>Configured like Program.cs configures the app's own.</summary>
    private sealed class DbContextFactory(string connectionString) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MapAppDbEnums())
                .UseSnakeCaseNamingConvention()
                .Options;

        public AppDbContext CreateDbContext() => new(_options);
    }
}
