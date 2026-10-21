using EventReservationApp.Models.Dtos;

namespace EventReservationApp.Services.Interfaces;

/// <summary>
/// Runs a reservation operation at most once per idempotency key. The Rasa actions
/// send the same key when they retry a call whose answer was lost (connection
/// dropped, timeout), so the retry gets the stored answer instead of running the
/// operation again.
/// </summary>
public interface IIdempotencyService
{
    /// <param name="key">The caller's Idempotency-Key header.</param>
    /// <param name="operation">One of <see cref="IdempotentOperations"/>.</param>
    /// <param name="resourceId">The event id (create) or reservation id (cancel).</param>
    /// <param name="operationToRun">The operation; run only if this key is new.</param>
    Task<IdempotentResult> ExecuteAsync(
        Guid key,
        string operation,
        int resourceId,
        Func<Task<ReservationOperationResult>> operationToRun);
}

public static class IdempotentOperations
{
    public const string ReservationCreate = "reservation.create";
    public const string ReservationCancel = "reservation.cancel";
}

public enum IdempotencyOutcome
{
    /// <summary>New key: the operation ran and its answer was stored.</summary>
    Executed,

    /// <summary>Repeated key: the operation did not run again; Result is the stored answer.</summary>
    Replayed,

    /// <summary>The key was already used for another operation or resource; nothing ran.</summary>
    KeyReusedForDifferentRequest
}

/// <param name="Result">The operation's answer; null for <see cref="IdempotencyOutcome.KeyReusedForDifferentRequest"/>.</param>
public sealed record IdempotentResult(IdempotencyOutcome Outcome, ReservationOperationResult? Result);
