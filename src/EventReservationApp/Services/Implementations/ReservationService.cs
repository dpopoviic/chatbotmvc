using EventReservationApp.Data;
using EventReservationApp.Models.Dtos;
using EventReservationApp.Models.Entities;
using EventReservationApp.Models.ViewModels;
using EventReservationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EventReservationApp.Services.Implementations;

public class ReservationService : IReservationService
{
    private readonly ApplicationDbContext _context;

    public ReservationService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<ReservationListItemViewModel>> GetAllAsync()
    {
        return await Projected()
            .OrderByDescending(r => r.ReservationDate)
            .ToListAsync();
    }

    public async Task<List<ReservationListItemViewModel>> GetForUserAsync(string userId)
    {
        return await Projected()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.ReservationDate)
            .ToListAsync();
    }

    public Task<ReservationListItemViewModel?> GetByIdAsync(int id) =>
        Projected().FirstOrDefaultAsync(r => r.Id == id);

    public async Task<(bool Success, string? ErrorMessage, ReservationErrorCode? ErrorCode)> CreateAsync(
        string userId, int eventId, string? notes)
    {
        var @event = await _context.Events
            .Include(e => e.EventReservations)
            .FirstOrDefaultAsync(e => e.Id == eventId);

        if (@event is null)
        {
            return (false, "The selected event could not be found.", ReservationErrorCode.EventNotFound);
        }

        if (@event.EventReservations.Any(r => r.UserId == userId))
        {
            return (false, "You already have a reservation for this event.", ReservationErrorCode.AlreadyReserved);
        }

        // Enforce capacity: reservations are blocked once an event is full.
        // Known limitation: two different users reserving the last place at the
        // same moment can both pass this check (the count is read without a lock),
        // so an event can end up one over capacity. If that ever matters, lock the
        // event row first (SELECT ... FROM Events WITH (UPDLOCK, ROWLOCK)) inside a
        // transaction.
        if (@event.EventReservations.Count >= @event.Capacity)
        {
            return (false, "This event has reached its capacity and can no longer accept reservations.",
                ReservationErrorCode.EventFull);
        }

        var reservation = new EventReservation
        {
            UserId = userId,
            EventId = eventId,
            ReservationDate = DateTime.UtcNow,
            Notes = notes
        };

        _context.EventReservations.Add(reservation);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A parallel request for the same user and event (double click, or a
            // retry from the chatbot) saved first and this insert hit the unique
            // (UserId, EventId) index. The failed row is detached so a later
            // SaveChanges on this context does not try to insert it again.
            _context.Entry(reservation).State = EntityState.Detached;

            // Checking the database instead of the provider's error number works
            // on any provider; any other failure is not hidden.
            var alreadyReserved = await _context.EventReservations
                .AnyAsync(r => r.UserId == userId && r.EventId == eventId);
            if (!alreadyReserved)
            {
                throw;
            }

            return (false, "You already have a reservation for this event.", ReservationErrorCode.AlreadyReserved);
        }

        return (true, null, null);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var reservation = await _context.EventReservations.FirstOrDefaultAsync(r => r.Id == id);
        if (reservation is null)
        {
            return false;
        }

        _context.EventReservations.Remove(reservation);
        try
        {
            await _context.SaveChangesAsync();

        }
        catch (DbUpdateException)
        {
            _context.Entry(reservation).State = EntityState.Detached;
            throw;
        }
        return true;
    }

    public Task<bool> IsOwnedByUserAsync(int reservationId, string userId) =>
        _context.EventReservations.AnyAsync(r => r.Id == reservationId && r.UserId == userId);

    private IQueryable<ReservationListItemViewModel> Projected() =>
        _context.EventReservations
            .Include(r => r.Event)
            .Include(r => r.User)
            .Select(r => new ReservationListItemViewModel
            {
                Id = r.Id,
                EventId = r.EventId,
                EventName = r.Event!.Name,
                EventStartDate = r.Event.StartDate,
                EventEndDate = r.Event.EndDate,
                EventLocation = r.Event.Location,
                UserId = r.UserId,
                UserFullName = (r.User!.FirstName + " " + r.User.LastName).Trim(),
                UserEmail = r.User.Email ?? string.Empty,
                ReservationDate = r.ReservationDate,
                Notes = r.Notes
            });
}
