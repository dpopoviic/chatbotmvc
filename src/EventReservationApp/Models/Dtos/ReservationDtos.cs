using System.Text.Json.Serialization;

namespace EventReservationApp.Models.Dtos
{
    public class MyReservationDto
    {
        public int ReservationId { get; set; }
        public int EventId { get; set; }
        public string EventName { get; set; } = string.Empty;
        public DateTime EventStartDate { get; set; }
        public DateTime EventEndDate { get; set; }
        public string EventLocation { get; set; } = string.Empty;
        public DateTime ReservationDate { get; set; }
        public string? Notes { get; set; }
    }
    /// <summary>
    /// Why a reservation operation failed. Callers (the Rasa actions) decide by
    /// this code, not by ErrorMessage text, so messages can change freely.
    /// Serialized as its name ("AlreadyReserved"), not as a number.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ReservationErrorCode>))]
    public enum ReservationErrorCode
    {
        EventNotFound,
        AlreadyReserved,
        EventFull,
        ReservationNotFound
    }

    public class ReservationOperationResult
    {
        public bool Success { get; set; }
        public ReservationErrorCode? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public int? ReservationId { get; set; }

        public static ReservationOperationResult Ok(int? reservationId = null) =>
            new() { Success = true, ReservationId = reservationId };

        public static ReservationOperationResult Fail(ReservationErrorCode errorCode, string errorMessage) =>
            new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
    }
}
