using EventReservationApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace EventReservationApp.Tests.TestHelpers;

/// <summary>
/// Creates a fresh, isolated in-memory <see cref="ApplicationDbContext"/>
/// for a single test. A new Guid-named database per call guarantees tests
/// never see each other's data, even when the test runner executes them
/// in parallel.
/// </summary>
public static class InMemoryDbContextFactory
{
    public static ApplicationDbContext Create()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }

    /// <summary>
    /// A context on a named in-memory database, for tests that need several
    /// contexts on the same data (e.g. a parallel request). Contexts created with
    /// the same <paramref name="databaseName"/> and <paramref name="root"/> share it.
    /// </summary>
    public static ApplicationDbContext Create(
        string databaseName,
        InMemoryDatabaseRoot root,
        params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName, root)
            .AddInterceptors(interceptors)
            .Options;

        return new ApplicationDbContext(options);
    }
}
