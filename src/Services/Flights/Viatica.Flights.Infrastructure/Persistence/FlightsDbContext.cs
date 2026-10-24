using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Viatica.Contracts;
using Viatica.Flights.Application;
using Viatica.Flights.Domain;
using Viatica.Kernel.Messaging;

namespace Viatica.Flights.Infrastructure.Persistence;

/// <summary>
/// The Flights database: flight orders, and the outbox and inbox every service carries (docs/messaging.md). Tables
/// and columns are snake_case, spelled out here because the repository does not use a naming convention package.
/// </summary>
public sealed class FlightsDbContext(DbContextOptions<FlightsDbContext> options) : DbContext(options), IFlightsDb
{
    public const string Schema = "flights";

    public DbSet<FlightOrder> Orders => Set<FlightOrder>();

    public void Publish(IIntegrationEvent message) => this.AddIntegrationEvent(message);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddMessaging();

        modelBuilder.Entity<FlightOrder>(order =>
        {
            order.ToTable("flight_orders");
            order.HasKey(o => o.Id);
            order.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
            order.Property(o => o.TripId).HasColumnName("trip_id");
            order.Property(o => o.ItemId).HasColumnName("item_id");
            order.Property(o => o.AirlineCode).HasColumnName("airline_code").HasMaxLength(2);
            order.Property(o => o.BookingReference).HasColumnName("booking_reference").HasMaxLength(32);
            order.Property(o => o.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(100);
            order.Property(o => o.OfferId).HasColumnName("offer_id").HasMaxLength(64);
            order.Property(o => o.TotalAmount).HasColumnName("total_amount").HasPrecision(12, 2);
            order.Property(o => o.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
            order.Property(o => o.TicketingTimeLimit).HasColumnName("ticketing_time_limit");
            order.Property(o => o.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
            order.Property(o => o.TicketNumbers).HasColumnName("ticket_numbers");
            order.Property(o => o.TicketingRefusal).HasColumnName("ticketing_refusal").HasMaxLength(500);
            order.Property(o => o.RefundAmount).HasColumnName("refund_amount").HasPrecision(12, 2);
            // Ciphertext of the passenger list (docs/design.md, "Personal data on the broker"); the key is not in the database.
            order.Property(o => o.SealedPassengers).HasColumnName("sealed_passengers");
            order.Property(o => o.CreatedAt).HasColumnName("created_at");
            order.Property(o => o.UpdatedAt).HasColumnName("updated_at");
            ConfigureVersion(order);

            order.Ignore(o => o.Total);
            order.Ignore(o => o.Refund);

            // One order per item, however often the request arrives. The handler checks first; this is the backstop
            // for two consumers that raced, and the lookup index for every later request about the item.
            order.HasIndex(o => o.ItemId).IsUnique().HasDatabaseName("ux_flight_orders_item_id");
            order.HasIndex(o => o.TripId).HasDatabaseName("ix_flight_orders_trip_id");
            order.HasIndex(o => new { o.AirlineCode, o.BookingReference }).IsUnique().HasDatabaseName("ux_flight_orders_booking_reference");
        });
    }

    // Postgres' own row version (xmin) as the concurrency token and the ETag: no extra column to keep in step.
    private static void ConfigureVersion(EntityTypeBuilder<FlightOrder> order) =>
        order.Property(o => o.Version).IsRowVersion();
}
