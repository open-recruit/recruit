using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecruIT.List.Data;

namespace RecruIT.List.Tests;

/// <summary>
/// The shared key ring Program.cs persists to the app database: what one replica protects (auth
/// cookies, antiforgery tokens), another replica must be able to unprotect.
/// </summary>
public sealed class DataProtectionKeyContextTests : IDisposable
{
    // SQLite's ":memory:" database lives as long as this one connection stays open.
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public DataProtectionKeyContextTests()
    {
        _connection.Open();
        using var db = new DataProtectionKeyContext(
            new DbContextOptionsBuilder<DataProtectionKeyContext>().UseSqlite(_connection).Options
        );
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void A_payload_protected_by_one_replica_unprotects_on_another()
    {
        using var replicaA = CreateReplica();
        using var replicaB = CreateReplica();

        var protectedPayload = Protector(replicaA).Protect("auth cookie");

        Assert.Equal("auth cookie", Protector(replicaB).Unprotect(protectedPayload));
    }

    [Fact]
    public void The_key_ring_is_stored_in_the_database()
    {
        using var replica = CreateReplica();
        Protector(replica).Protect("auth cookie");

        using var scope = replica.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataProtectionKeyContext>();
        Assert.NotEmpty(db.DataProtectionKeys);
    }

    private ServiceProvider CreateReplica()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DataProtectionKeyContext>(options => options.UseSqlite(_connection));
        services.AddDataProtection().PersistKeysToDbContext<DataProtectionKeyContext>();
        return services.BuildServiceProvider();
    }

    private static IDataProtector Protector(IServiceProvider replica) =>
        replica.GetRequiredService<IDataProtectionProvider>().CreateProtector("tests");
}
