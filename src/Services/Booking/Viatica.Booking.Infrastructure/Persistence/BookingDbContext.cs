using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Viatica.Booking.Application;
using Viatica.Booking.Domain;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;

namespace Viatica.Booking.Infrastructure.Persistence;

/// <summary>
/// The Booking database: trips and their items, and the outbox and inbox every service carries (docs/messaging.md).
/// Tables and columns are snake_case, spelled out here because the repository does not use a naming convention package.
/// </summary>
public sealed class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options), IBookingDb
{
    public const string Schema = "booking";

    public DbSet<Trip> Trips => Set<Trip>();

    public DbSet<TripItem> Items => Set<TripItem>();

    public void Publish(IIntegrationEvent message) => this.AddIntegrationEvent(message);

    public Task LockTripAsync(Guid tripId, CancellationToken cancellationToken) =>
        Database.ExecuteSqlAsync($"select 1 from booking.trips where id = {tripId} for update", cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddMessaging();

        modelBuilder.Entity<Trip>(trip =>
        {
            trip.ToTable("trips");
            trip.HasKey(t => t.Id);
            trip.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            trip.Property(t => t.TravellerId).HasColumnName("traveller_id");
            trip.Property(t => t.AccessKeyHash).HasColumnName("access_key_hash");
            trip.Property(t => t.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
            trip.Property(t => t.CreatedAt).HasColumnName("created_at");
            trip.Property(t => t.UpdatedAt).HasColumnName("updated_at");
            trip.Property(t => t.Revision).HasColumnName("revision");
            trip.Property(t => t.CheckoutFingerprint).HasColumnName("checkout_fingerprint").HasMaxLength(128);
            trip.Property(t => t.CheckoutStartedAt).HasColumnName("checkout_started_at");
            trip.Property(t => t.PaymentId).HasColumnName("payment_id");
            trip.Property(t => t.Payment).HasColumnName("payment_status").HasConversion<string>().HasMaxLength(24);
            trip.Property(t => t.PaymentAmount).HasColumnName("payment_amount").HasPrecision(12, 2);
            trip.Property(t => t.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
            // Ciphertext (AES-256-GCM, Booking's own key). Null for a draft, and again once the trip is confirmed or failed.
            trip.Property(t => t.SealedTravellerData).HasColumnName("sealed_traveller_data");
            trip.Property(t => t.Failure).HasColumnName("failure").HasConversion<string>().HasMaxLength(24);
            trip.Property(t => t.Deadline).HasColumnName("deadline");
            trip.Property(t => t.DeadlineMargin).HasColumnName("deadline_margin");
            trip.Property(t => t.AssumedHoldLifetime).HasColumnName("assumed_hold_lifetime");
            trip.Property(t => t.ResendAfter).HasColumnName("resend_after");
            trip.Property(t => t.CompensationSentAt).HasColumnName("compensation_sent_at");
            trip.Property(t => t.NextCheckAt).HasColumnName("next_check_at");
            ConfigureVersion(trip);

            trip.Ignore(t => t.CurrentTotal);
            trip.Ignore(t => t.RefundedTotal);

            trip.HasMany(t => t.Items).WithOne().HasForeignKey(i => i.TripId).OnDelete(DeleteBehavior.Cascade);
            trip.Navigation(t => t.Items).HasField("items").UsePropertyAccessMode(PropertyAccessMode.Field);

            // "My trips": the traveller's own, newest first.
            trip.HasIndex(t => new { t.TravellerId, t.CreatedAt }).HasDatabaseName("ix_trips_traveller_id_created_at");

            // The scheduled check's query. Almost every trip has nothing due, so only the ones that do are indexed.
            trip.HasIndex(t => t.NextCheckAt).HasDatabaseName("ix_trips_next_check_at").HasFilter("next_check_at is not null");
        });

        modelBuilder.Entity<TripItem>(item =>
        {
            item.ToTable("trip_items");
            item.HasKey(i => i.Id);
            item.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
            item.Property(i => i.TripId).HasColumnName("trip_id");
            item.Property(i => i.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(8);
            item.Property(i => i.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
            item.Property(i => i.Awaiting).HasColumnName("awaiting").HasConversion<string>().HasMaxLength(16);
            item.Property(i => i.Reference).HasColumnName("reference").HasMaxLength(2048);
            item.Property(i => i.Description).HasColumnName("description").HasMaxLength(200);
            item.Property(i => i.ExpectedAmount).HasColumnName("expected_amount").HasPrecision(12, 2);
            item.Property(i => i.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
            item.Property(i => i.Quantity).HasColumnName("quantity");
            item.Property(i => i.DriverAge).HasColumnName("driver_age");
            item.Property(i => i.QuoteExpiresAt).HasColumnName("quote_expires_at");
            item.Property(i => i.HoldId).HasColumnName("hold_id");
            item.Property(i => i.OrderId).HasColumnName("order_id");
            item.Property(i => i.TotalAmount).HasColumnName("total_amount").HasPrecision(12, 2);
            item.Property(i => i.ExpiresAt).HasColumnName("expires_at");
            item.Property(i => i.PartnerId).HasColumnName("partner_id");
            item.Property(i => i.StartsAt).HasColumnName("starts_at");
            item.Property(i => i.Units).HasColumnName("units");
            item.Property(i => i.FreeCancellationHours).HasColumnName("free_cancellation_hours");
            item.Property(i => i.Penalty).HasColumnName("penalty").HasConversion<string>().HasMaxLength(16);
            item.Property(i => i.Refusal).HasColumnName("refusal").HasMaxLength(500);
            item.Property(i => i.Cancel).HasColumnName("cancel_state").HasConversion<string>().HasMaxLength(16);
            item.Property(i => i.CancelRequestedAt).HasColumnName("cancel_requested_at");
            item.Property(i => i.CancelDeadline).HasColumnName("cancel_deadline");
            item.Property(i => i.RefundId).HasColumnName("refund_id");
            item.Property(i => i.RefundAmount).HasColumnName("refund_amount").HasPrecision(12, 2);
            item.Property(i => i.Refund).HasColumnName("refund_status").HasConversion<string>().HasMaxLength(16);
            item.Property(i => i.AddedAt).HasColumnName("added_at");

            item.Ignore(i => i.Total);
            item.Ignore(i => i.ExpectedTotal);

            item.HasIndex(i => i.TripId).HasDatabaseName("ix_trip_items_trip_id");

            // An answer names the hold or the order, and a hold or an order belongs to one item.
            item.HasIndex(i => i.HoldId).IsUnique().HasDatabaseName("ux_trip_items_hold_id");

            // The partner's list of bookings against their own inventory, newest first.
            item.HasIndex(i => new { i.PartnerId, i.AddedAt }).HasDatabaseName("ix_trip_items_partner_id_added_at").HasFilter("partner_id is not null");
        });
    }

    // Postgres' own row version (xmin) as the concurrency token and the ETag: no extra column to keep in step.
    private static void ConfigureVersion(EntityTypeBuilder<Trip> trip) =>
        trip.Property(t => t.Version).IsRowVersion();
}
