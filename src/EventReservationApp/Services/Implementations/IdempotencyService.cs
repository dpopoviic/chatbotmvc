using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventReservationApp.Data;
using EventReservationApp.Models.Dtos;
using EventReservationApp.Models.Entities;
using EventReservationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EventReservationApp.Services.Implementations;

public class IdempotencyService : IIdempotencyService
{
    // How long a key is remembered. Rasa retries within seconds, and every chat
    // message gets new keys, so a day is plenty.
    private static readonly TimeSpan RecordLifetime = TimeSpan.FromHours(24);
    private readonly ApplicationDbContext _context;
    private readonly ICurrentUser _currentUser;

    public IdempotencyService(ApplicationDbContext context, ICurrentUser currentUser)
    {
        _context = context;
        _currentUser = currentUser;
    }

    public async Task<IdempotentResult> ExecuteAsync(
        Guid key,
        string operation,
        int resourceId,
        Func<Task<ReservationOperationResult>> operationToRun)
    {
        // The key belongs to the user from the verified token, so nobody can
        // collide with, or read, another user's stored answer.
        var userId = _currentUser.RequireUserId();
        var requestHash = ComputeRequestHash(operation, resourceId);
        var now = DateTime.UtcNow;

        await using var transaction = await _context.Database.BeginTransactionAsync();

        // 1. Claim the key before doing anything. If a request with the same key
        //    is still running, SQL Server makes this insert wait for it: when that
        //    one commits, this insert fails as a duplicate; if it rolls back, this
        //    one goes ahead. The answer is filled in at step 3.
        var record = new IdempotencyRecord
        {
            UserId = userId,
            Key = key,
            Operation = operation,
            RequestHash = requestHash,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(RecordLifetime)
        };
        _context.IdempotencyRecords.Add(record);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _context.Entry(record).State = EntityState.Detached;
            await transaction.RollbackAsync();

            var existing = await _context.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.UserId == userId && r.Key == key);
            if (existing is null)
            {
                throw; // not a duplicate key - a real failure, not hidden
            }

            return existing.RequestHash == requestHash
                ? new IdempotentResult(
                    IdempotencyOutcome.Replayed,
                    JsonSerializer.Deserialize<ReservationOperationResult>(existing.ResponseJson))
                : new IdempotentResult(IdempotencyOutcome.KeyReusedForDifferentRequest, null);
        }

        // 2. Run the operation in the same transaction. If it throws, the
        //    transaction is rolled back with the key, so a retry runs it again.
        var result = await operationToRun();

        // 3. Store the answer and commit: the operation's changes and the answer
        //    are saved together, or neither is.
        record.ResponseJson = JsonSerializer.Serialize(result);
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();

        return new IdempotentResult(IdempotencyOutcome.Executed, result);
    }

    private static string ComputeRequestHash(string operation, int resourceId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{operation}:{resourceId}")));
}
