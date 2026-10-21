using System.Text.Json;
using EventReservationApp.Models.Dtos;
using EventReservationApp.Services.Implementations;
using EventReservationApp.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace EventReservationApp.Tests;

public class MyReservationsServiceTests
{
    private static (MyReservationsService Service, EventReservationApp.Data.ApplicationDbContext Db) CreateService(
        EventReservationApp.Data.ApplicationDbContext db,
        EventReservationApp.Services.Interfaces.ICurrentUser currentUser)
    {
        var reservationService = new ReservationService(db);
        var service = new MyReservationsService(reservationService, currentUser);
        return (service, db);
    }

    // -----------------------------------------------------------------
    // GetMyReservationsAsync
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetMyReservationsAsync_ReturnsOnlyCurrentUsersReservations()
    {
        await using var db = InMemoryDbContextFactory.Create();
        db.AddUser("user-1");
        db.AddUser("user-2");
        db.AddEvent(1, name: "Event A");
        db.AddEvent(2, name: "Event B");
        db.AddReservation(1, "user-1", 1);
        db.AddReservation(2, "user-2", 2); // belongs to a different user
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, FakeCurrentUser.For("user-1"));

        var reservations = await service.GetMyReservationsAsync();

        var reservation = Assert.Single(reservations);
        Assert.Equal("Event A", reservation.EventName);
    }

    [Fact]
    public async Task GetMyReservationsAsync_IncludesEventEndDateAndLocation()
    {
        await using var db = InMemoryDbContextFactory.Create();
        db.AddUser("user-1");
        var start = new DateTime(2026, 10, 15, 18, 0, 0);
        var end = new DateTime(2026, 10, 15, 21, 30, 0);
        db.AddEvent(1, name: "Event A", location: "Beograd", startDate: start, endDate: end);
        db.AddReservation(1, "user-1", 1);
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, FakeCurrentUser.For("user-1"));

        var reservation = Assert.Single(await service.GetMyReservationsAsync());
        Assert.Equal(start, reservation.EventStartDate);
        Assert.Equal(end, reservation.EventEndDate);
        Assert.Equal("Beograd", reservation.EventLocation);
    }

    [Fact]
    public async Task GetMyReservationsAsync_Unauthenticated_ThrowsUnauthorizedAccessException()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var (service, _) = CreateService(db, FakeCurrentUser.Anonymous());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetMyReservationsAsync());
    }

    // -----------------------------------------------------------------
    // ReserveForCurrentUserAsync
    // -----------------------------------------------------------------

    [Fact]
    public async Task ReserveForCurrentUserAsync_ValidRequest_CreatesReservationForCurrentUser()
    {
        await using var db = InMemoryDbContextFactory.Create();
        db.AddUser("user-1");
        db.AddEvent(1, name: "Event A", capacity: 5);
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, FakeCurrentUser.For("user-1"));

        var result = await service.ReserveForCurrentUserAsync(1, "Looking forward to it");

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);

        var reservations = await service.GetMyReservationsAsync();
        var reservation = Assert.Single(reservations);
        Assert.Equal(1, reservation.EventId);
        Assert.Equal("Looking forward to it", reservation.Notes);
    }

    [Fact]
    public async Task ReserveForCurrentUserAsync_DuplicateReservation_IsRejected()
    {
        await using var db = InMemoryDbContextFactory.Create();
        db.AddUser("user-1");
        db.AddEvent(1, name: "Event A", capacity: 5);
        db.AddReservation(1, "user-1", 1);
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, FakeCurrentUser.For("user-1"));

        var result = await service.ReserveForCurrentUserAsync(1, null);

        Assert.False(result.Success);
        Assert.Equal(ReservationErrorCode.AlreadyReserved, result.ErrorCode);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ReserveForCurrentUserAsync_EventAtCapacity_IsRejected()
    {
        await using var db = InMemoryDbContextFactory.Create();
        db.AddUser("user-1");
        db.AddUser("user-2");
        db.AddEvent(1, name: "Full Event", capacity: 1);
        db.AddReservation(1, "user-1", 1);
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, FakeCurrentUser.For("user-2"));

        var result = await service.ReserveForCurrentUserAsync(1, null);

        Assert.False(result.Success);
        Assert.Equal(ReservationErrorCode.EventFull, result.ErrorCode);
    }

    [Fact]
    public async Task ReserveForCurrentUserAsync_UnknownEvent_ReturnsEventNotFound()
    {
        await using var db = InMemoryDbContextFactory.Create();
        db.AddUser("user-1");
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, FakeCurrentUser.For("user-1"));

        var result = await service.ReserveForCurrentUserAsync(999, null);

        Assert.False(result.Success);
        Assert.Equal(ReservationErrorCode.EventNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task ReserveForCurrentUserAsync_Unauthenticated_ThrowsUnauthorizedAccessException()
    {
        await using var db = InMemoryDbContextFactory.Create();
        db.AddEvent(1, name: "Event A");
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, FakeCurrentUser.Anonymous());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ReserveForCurrentUserAsync(1, null));
    }

    [Fact]
    public async Task ReserveForCurrentUserAsync_ParallelDuplicateSavedFirst_ReturnsAlreadyReservedInsteadOfThrowing()
    {
        var databaseName = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();
        await using (var seed = InMemoryDbContextFactory.Create(databaseName, root))
        {
            seed.AddUser("user-1");
            seed.AddEvent(1, name: "Event A", capacity: 5);
            await seed.SaveChangesAsync();
        }

        // A parallel request for the same user and event saves its reservation
        // first; this save then fails the way the unique (UserId, EventId) index would.
        var interceptor = new FailingSaveChangesInterceptor(async () =>
        {
            await using var parallel = InMemoryDbContextFactory.Create(databaseName, root);
            parallel.AddReservation(100, "user-1", 1);
            await parallel.SaveChangesAsync();
        });
        await using var db = InMemoryDbContextFactory.Create(databaseName, root, interceptor);
        var (service, _) = CreateService(db, FakeCurrentUser.For("user-1"));

        var result = await service.ReserveForCurrentUserAsync(1, null);

        Assert.False(result.Success);
        Assert.Equal(ReservationErrorCode.AlreadyReserved, result.ErrorCode);
        Assert.Equal("You already have a reservation for this event.", result.ErrorMessage);
        await using var check = InMemoryDbContextFactory.Create(databaseName, root);
        Assert.Equal(1, await check.EventReservations.CountAsync(r => r.UserId == "user-1" && r.EventId == 1));
    }

    [Fact]
    public async Task ReserveForCurrentUserAsync_SaveFailsForOtherReason_Throws()
    {
        var databaseName = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();
        await using (var seed = InMemoryDbContextFactory.Create(databaseName, root))
        {
            seed.AddUser("user-1");
            seed.AddEvent(1, name: "Event A", capacity: 5);
            await seed.SaveChangesAsync();
        }

        // The save fails but no reservation exists - not a duplicate, so it must not be hidden.
        await using var db = InMemoryDbContextFactory.Create(
            databaseName, root, new FailingSaveChangesInterceptor());
        var (service, _) = CreateService(db, FakeCurrentUser.For("user-1"));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => service.ReserveForCurrentUserAsync(1, null));
    }

    // -----------------------------------------------------------------
    // CancelForCurrentUserAsync
    // -----------------------------------------------------------------

    [Fact]
    public async Task CancelForCurrentUserAsync_OwnReservation_Cancels()
    {
        await using var db = InMemoryDbContextFactory.Create();
        db.AddUser("user-1");
        db.AddEvent(1, name: "Event A");
        db.AddReservation(42, "user-1", 1);
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, FakeCurrentUser.For("user-1"));

        var result = await service.CancelForCurrentUserAsync(42);

        Assert.True(result.Success);

        var remaining = await service.GetMyReservationsAsync();
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task CancelForCurrentUserAsync_AnotherUsersReservation_IsRejectedAndNotDeleted()
    {
        await using var db = InMemoryDbContextFactory.Create();
        db.AddUser("owner");
        db.AddUser("attacker");
        db.AddEvent(1, name: "Event A");
        db.AddReservation(42, "owner", 1);
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, FakeCurrentUser.For("attacker"));

        var result = await service.CancelForCurrentUserAsync(42);

        Assert.False(result.Success);
        Assert.Equal(ReservationErrorCode.ReservationNotFound, result.ErrorCode);

        // The reservation must still exist, untouched, for its real owner.
        var ownerService = CreateService(db, FakeCurrentUser.For("owner")).Service;
        var ownerReservations = await ownerService.GetMyReservationsAsync();
        Assert.Single(ownerReservations);
    }

    [Fact]
    public async Task CancelForCurrentUserAsync_NonexistentReservation_IsRejected()
    {
        await using var db = InMemoryDbContextFactory.Create();
        db.AddUser("user-1");
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, FakeCurrentUser.For("user-1"));

        var result = await service.CancelForCurrentUserAsync(999);

        Assert.False(result.Success);
        Assert.Equal(ReservationErrorCode.ReservationNotFound, result.ErrorCode);
    }

    [Fact]
    public void ReservationOperationResult_SerializesErrorCodeAsName()
    {
        // The Rasa actions read "errorCode" from the internal API's JSON, so it
        // must arrive as the name, not as the enum's number.
        var json = JsonSerializer.Serialize(
            ReservationOperationResult.Fail(ReservationErrorCode.AlreadyReserved, "msg"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"errorCode\":\"AlreadyReserved\"", json);
    }

    [Fact]
    public async Task CancelForCurrentUserAsync_Unauthenticated_ThrowsUnauthorizedAccessException()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var (service, _) = CreateService(db, FakeCurrentUser.Anonymous());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CancelForCurrentUserAsync(1));
    }
}
