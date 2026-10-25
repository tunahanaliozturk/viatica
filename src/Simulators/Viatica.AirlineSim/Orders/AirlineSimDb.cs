using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Viatica.AirlineSim.Orders;

/// <summary>Seats left in one fare class of one flight on one date.</summary>
/// <remarks>
/// Rows are created on first sale with the class's seed capacity. A missing row means nothing was sold yet, which
/// keeps the table as small as the traffic instead of every flight times every date of the year.
/// </remarks>
internal sealed class SeatBucket
{
    public required string Airline { get; init; }

    public required string FlightNumber { get; init; }

    public DateOnly FlightDate { get; init; }

    public char FareClass { get; init; }

    public int Remaining { get; init; }
}

internal sealed class AirlineSimDb(DbContextOptions<AirlineSimDb> options) : DbContext(options)
{
    public const string IdempotencyKeyIndex = "ux_orders_idempotency_key";

    public const string BookingReferenceIndex = "ux_orders_booking_reference";

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<SeatBucket> SeatBuckets => Set<SeatBucket>();

    /// <summary>
    /// Takes <paramref name="seats"/> from a bucket if, and only if, that many are left. One conditional UPDATE is
    /// the whole guard: Postgres locks the row, a concurrent buyer waits and then re-checks the condition against
    /// the new count, so the last seat goes to exactly one caller and no read-then-write window exists to oversell in.
    /// </summary>
    public async Task<bool> TryTakeSeatsAsync(string airline, string flightNumber, DateOnly date, char fareClass, int capacity, int seats, CancellationToken cancellationToken)
    {
        await Database.ExecuteSqlAsync(
            $"""
            insert into seat_buckets (airline, flight_number, flight_date, fare_class, remaining)
            values ({airline}, {flightNumber}, {date}, {fareClass}, {capacity})
            on conflict do nothing
            """,
            cancellationToken);

        int updated = await SeatBuckets
            .Where(b => b.Airline == airline && b.FlightNumber == flightNumber && b.FlightDate == date &&
                        b.FareClass == fareClass && b.Remaining >= seats)
            .ExecuteUpdateAsync(setters => setters.SetProperty(b => b.Remaining, b => b.Remaining - seats), cancellationToken);

        return updated == 1;
    }

    public Task ReturnSeatsAsync(string airline, string flightNumber, DateOnly date, char fareClass, int seats, CancellationToken cancellationToken) =>
        SeatBuckets
            .Where(b => b.Airline == airline && b.FlightNumber == flightNumber && b.FlightDate == date && b.FareClass == fareClass)
            .ExecuteUpdateAsync(setters => setters.SetProperty(b => b.Remaining, b => b.Remaining + seats), cancellationToken);

    public static bool IsIdempotencyKeyClash(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IdempotencyKeyIndex };

    public static bool IsBookingReferenceClash(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: BookingReferenceIndex };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SeatBucket>(bucket =>
        {
            bucket.ToTable("seat_buckets");
            bucket.HasKey(b => new { b.Airline, b.FlightNumber, b.FlightDate, b.FareClass });
            bucket.ToTable(table => table.HasCheckConstraint("ck_seat_buckets_remaining", "remaining >= 0"));
        });

        modelBuilder.Entity<Order>(order =>
        {
            order.ToTable("orders");
            order.HasKey(o => o.Id);
            order.HasIndex(o => new { o.Airline, o.BookingReference }).IsUnique().HasDatabaseName(BookingReferenceIndex);
            order.HasIndex(o => new { o.Airline, o.IdempotencyKey }).IsUnique().HasDatabaseName(IdempotencyKeyIndex);
            order.Property(o => o.Status).HasConversion<string>();
            order.Property(o => o.Version).IsRowVersion();
            order.Ignore(o => o.Seats);
        });

        // snake_case columns, so the raw SQL above and a person at psql read the same names.
        foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableProperty property in entity.GetProperties())
            {
                if (property.Name != nameof(Order.Version))
                {
                    property.SetColumnName(SnakeCase(property.Name));
                }
            }
        }
    }

    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        foreach (char c in name)
        {
            if (char.IsUpper(c) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
