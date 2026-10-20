using EventReservationApp.Data;
using Microsoft.EntityFrameworkCore;

namespace EventReservationApp.Tests.TestHelpers;

/// <summary>
/// A throwaway SQL Server LocalDB database for tests that need what the in-memory
/// provider does not have: unique indexes, transactions and real locking (e.g. a
/// second insert of the same key waiting for the first transaction). Created from
/// the model on <see cref="CreateAsync"/>, deleted on dispose.
/// </summary>
public sealed class LocalDbTestDatabase : IAsyncDisposable
{
    private readonly string _connectionString;

    private LocalDbTestDatabase(string connectionString)
    {
        _connectionString = connectionString;
    }

    public static async Task<LocalDbTestDatabase> CreateAsync()
    {
        var database = new LocalDbTestDatabase(
            $@"Server=(localdb)\MSSQLLocalDB;Database=EventReservationApp_Tests_{Guid.NewGuid():N};" +
            "Trusted_Connection=True;TrustServerCertificate=True");

        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        return database;
    }

    /// <summary>A new context, like the one a single HTTP request gets.</summary>
    public ApplicationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(_connectionString).Options);

    public async ValueTask DisposeAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }
}
