using EventReservationApp.Models.Dtos;
using EventReservationApp.Services.Auth;
using EventReservationApp.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventReservationApp.Controllers.Api;

/// Interni API na posebnom Kestrel endpoint-u ili portu, 
/// sa RequireHost na kontroleru ili mrežnim pravilom, 
/// tako da je dostupan samo iz Rasine mreže.

[ApiController]
[Route("api/internal")]
[Authorize(AuthenticationSchemes = InternalApiKeyHandler.SchemeName)]
public class InternalAgentController : ControllerBase
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string IdempotentReplayedHeader = "Idempotent-Replayed";
    private readonly IEventCatalogService _eventCatalogService;
    private readonly IMyReservationsService _myReservationsService;
    private readonly IIdempotencyService _idempotencyService;

    public InternalAgentController(
        IEventCatalogService eventCatalogService,
        IMyReservationsService myReservationsService,
        IIdempotencyService idempotencyService)
    {
        _eventCatalogService = eventCatalogService;
        _myReservationsService = myReservationsService;
        _idempotencyService = idempotencyService;
    }

    // GET /api/internal/events/search?searchTerm=&startDate=&endDate=&location=&availability=
    [HttpGet("events/search")]
    public async Task<ActionResult<List<EventSearchResultDto>>> SearchEvents(
        [FromQuery] string? searchTerm,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] string? location,
        [FromQuery] bool? availability,
        CancellationToken cancellationToken)
    {
        var filter = new EventSearchFilter
        {
            SearchTerm = searchTerm,
            StartDateFrom = startDate,
            StartDateTo = endDate,
            Location = location,
            OnlyAvailable = availability
        };

        var results = await _eventCatalogService.SearchEventsAsync(filter, cancellationToken);
        return Ok(results);
    }

    // GET /api/internal/events/resolve?name=
    [HttpGet("events/resolve")]
    public async Task<ActionResult<IReadOnlyList<EventCandidateDto>>> ResolveEvent(
        [FromQuery] string name,
        CancellationToken cancellationToken)
    {
        var candidates = await _eventCatalogService.ResolveByNameAsync(name, cancellationToken);
        return Ok(candidates);
    }

    // GET /api/internal/events/{eventId}/availability
    [HttpGet("events/{eventId:int}/availability")]
    public async Task<ActionResult<EventAvailabilityDto>> GetEventAvailability(
        int eventId,
        CancellationToken cancellationToken)
    {
        var availability = await _eventCatalogService.GetEventAvailabilityAsync(eventId, cancellationToken);
        return availability is null ? NotFound() : Ok(availability);
    }

    // GET /api/internal/reservations
    [HttpGet("reservations")]
    public async Task<ActionResult<List<MyReservationDto>>> GetMyReservations(
        CancellationToken cancellationToken)
    {
        var reservations = await _myReservationsService.GetMyReservationsAsync(cancellationToken);
        return Ok(reservations);
    }

    // POST /api/internal/reservations  { eventId, notes }
    // Required header Idempotency-Key: <guid> - a repeated key gets the stored answer.
    [HttpPost("reservations")]
    public async Task<ActionResult<ReservationOperationResult>> Reserve(
        [FromBody] ReserveRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] Guid? idempotencyKey)
    {
        if (idempotencyKey is not { } key || key == Guid.Empty)
        {
            return MissingIdempotencyKey();
        }

        // CancellationToken.None: once started, the reservation is finished and its
        // answer stored even if the caller has given up waiting - its retry then
        // simply gets that answer.
        var idempotent = await _idempotencyService.ExecuteAsync(
            key,
            IdempotentOperations.ReservationCreate,
            request.EventId,
            () => _myReservationsService.ReserveForCurrentUserAsync(
                request.EventId, request.Notes, CancellationToken.None));
        return ToActionResult(idempotent);
    }

    // DELETE /api/internal/reservations/{reservationId}
    // Required header Idempotency-Key: <guid> - a repeated key gets the stored answer.
    [HttpDelete("reservations/{reservationId:int}")]
    public async Task<ActionResult<ReservationOperationResult>> Cancel(
        int reservationId,
        [FromHeader(Name = IdempotencyKeyHeader)] Guid? idempotencyKey)
    {
        if (idempotencyKey is not { } key || key == Guid.Empty)
        {
            return MissingIdempotencyKey();
        }

        // Without the key, repeating a cancel that already succeeded answers
        // "Reservation not found."; with it, the retry gets the original success.
        var idempotent = await _idempotencyService.ExecuteAsync(
            key,
            IdempotentOperations.ReservationCancel,
            reservationId,
            () => _myReservationsService.CancelForCurrentUserAsync(reservationId, CancellationToken.None));
        return ToActionResult(idempotent);
    }

    // Every write must carry a key: without one a retry after a lost answer could
    // run the operation twice. An invalid GUID never gets here - model binding
    // already answers 400 for it.
    private BadRequestObjectResult MissingIdempotencyKey() =>
        BadRequest(new { error = $"The {IdempotencyKeyHeader} header (a GUID) is required." });

    private ActionResult<ReservationOperationResult> ToActionResult(IdempotentResult idempotent)
    {
        if (idempotent.Outcome == IdempotencyOutcome.KeyReusedForDifferentRequest)
        {
            // Rasa builds keys from the message id, action and resource, so this
            // means a bug on the caller's side - never run something else for it.
            return UnprocessableEntity(new { error = "Idempotency-Key was already used for a different request." });
        }

        if (idempotent.Outcome == IdempotencyOutcome.Replayed)
        {
            Response.Headers[IdempotentReplayedHeader] = "true";
        }

        return Ok(idempotent.Result);
    }

    public sealed class ReserveRequest
    {
        public int EventId { get; set; }
        public string? Notes { get; set; }
    }
}
