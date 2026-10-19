using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EventReservationApp.Tests.TestHelpers;

/// <summary>
/// Makes every SaveChanges on the context fail with a <see cref="DbUpdateException"/>,
/// the way SQL Server rejects an insert that violates a unique index. The in-memory
/// provider does not enforce unique indexes, so tests use this to reproduce that case.
/// <paramref name="beforeThrow"/> runs first - e.g. to save the "winning" row of a
/// parallel request through another context.
/// </summary>
public sealed class FailingSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly Func<Task>? _beforeThrow;

    public FailingSaveChangesInterceptor(Func<Task>? beforeThrow = null)
    {
        _beforeThrow = beforeThrow;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (_beforeThrow is not null)
        {
            await _beforeThrow();
        }

        throw new DbUpdateException("Simulated unique index violation.");
    }
}
