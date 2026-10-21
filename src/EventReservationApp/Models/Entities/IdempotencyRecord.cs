namespace EventReservationApp.Models.Entities;

/// <summary>
/// Remembers that a user already ran an operation with a given idempotency key,
/// and what it answered. When the same request comes again (a retry after a lost
/// connection), the stored answer is returned instead of running it a second time.
/// See Services/Implementations/IdempotencyService.cs.
/// </summary>
public class IdempotencyRecord
{
    public long Id { get; set; }

    /// <summary>The user from the verified token - keys are unique per user.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The Idempotency-Key header sent by the caller.</summary>
    public Guid Key { get; set; }

    /// <summary>e.g. "reservation.create" - tells two operations apart in the hash and in queries.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the operation and its resource id. A key reused for a different
    /// request is detected by a different hash. Notes and other text the user
    /// typed are not part of it.
    /// </summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>The answer of the first run, returned unchanged on every repeat.</summary>
    public string ResponseJson { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>After this the record may be deleted (cleanup comes later).</summary>
    public DateTime ExpiresAtUtc { get; set; }
}
