using Microsoft.EntityFrameworkCore;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Payments.Application;
using Viatica.Payments.Domain;

namespace Viatica.Payments.Infrastructure.Persistence;

/// <summary>
/// The Payments database: payments, the methods Booking carries tokens for, the record of every concluded operation,
/// and the outbox and inbox every service carries (docs/messaging.md). Tables and columns are snake_case, spelled out
/// here because the repository does not use a naming convention package.
/// </summary>
/// <remarks>
/// There is no column anywhere that could hold a card number, and the integration tests scan every text column of
/// every table after a run to prove none does.
/// </remarks>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options), IPaymentsDb
{
    public const string Schema = "payments";

    // Four decimals hold every ISO 4217 currency, so the arithmetic the money rules do in memory is the arithmetic
    // the database stores.
    private const int AmountPrecision = 14;
    private const int AmountScale = 4;

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<PaymentMethod> Methods => Set<PaymentMethod>();

    public DbSet<OperationRecord> Operations => Set<OperationRecord>();

    public void Publish(IIntegrationEvent message) => this.AddIntegrationEvent(message);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddMessaging();

        modelBuilder.Entity<Payment>(payment =>
        {
            payment.ToTable("payments");
            payment.HasKey(p => p.Id);
            payment.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            payment.Property(p => p.TripId).HasColumnName("trip_id");
            payment.Property(p => p.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
            payment.Property(p => p.AuthorizedAmount).HasColumnName("authorized_amount").HasPrecision(AmountPrecision, AmountScale);
            payment.Property(p => p.CapturedAmount).HasColumnName("captured_amount").HasPrecision(AmountPrecision, AmountScale);
            payment.Property(p => p.RefundedAmount).HasColumnName("refunded_amount").HasPrecision(AmountPrecision, AmountScale);
            payment.Property(p => p.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
            payment.Property(p => p.ProviderIntentId).HasColumnName("provider_intent_id").HasMaxLength(64);
            payment.Property(p => p.PaymentMethodToken).HasColumnName("payment_method_token").HasMaxLength(64);
            payment.Property(p => p.DeclineReason).HasColumnName("decline_reason").HasMaxLength(100);
            payment.Property(p => p.CreatedAt).HasColumnName("created_at");
            payment.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            // Postgres' own row version (xmin) as the concurrency token and the ETag: no extra column to keep in step.
            payment.Property(p => p.Version).IsRowVersion();

            payment.Ignore(p => p.Authorized);
            payment.Ignore(p => p.Captured);
            payment.Ignore(p => p.Refunded);

            // One payment per trip, however many payment ids Booking sends. The handler checks first; this is the
            // backstop for two consumers that raced.
            payment.HasIndex(p => p.TripId).IsUnique().HasDatabaseName("ux_payments_trip_id");
        });

        modelBuilder.Entity<PaymentMethod>(method =>
        {
            method.ToTable("payment_methods");
            method.HasKey(m => m.Token);
            method.Property(m => m.Token).HasColumnName("token").HasMaxLength(64).ValueGeneratedNever();
            method.Property(m => m.ProviderToken).HasColumnName("provider_token").HasMaxLength(64);
            method.Property(m => m.Brand).HasColumnName("brand").HasMaxLength(16);
            method.Property(m => m.Last4).HasColumnName("last4").HasMaxLength(4).IsFixedLength();
            method.Property(m => m.ExpMonth).HasColumnName("exp_month");
            method.Property(m => m.ExpYear).HasColumnName("exp_year");
            method.Property(m => m.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<OperationRecord>(operation =>
        {
            operation.ToTable("payment_operations");
            operation.HasKey(o => o.Id);
            operation.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
            operation.Property(o => o.TripId).HasColumnName("trip_id");
            operation.Property(o => o.PaymentId).HasColumnName("payment_id");
            operation.Property(o => o.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
            operation.Property(o => o.OperationId).HasColumnName("operation_id");
            operation.Property(o => o.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(100);
            operation.Property(o => o.Amount).HasColumnName("amount").HasPrecision(AmountPrecision, AmountScale);
            operation.Property(o => o.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
            operation.Property(o => o.Result).HasColumnName("result").HasConversion<string>().HasMaxLength(16);
            operation.Property(o => o.Reason).HasColumnName("reason").HasMaxLength(100);
            operation.Property(o => o.ProviderReference).HasColumnName("provider_reference").HasMaxLength(64);
            operation.Property(o => o.ResolvedByLookup).HasColumnName("resolved_by_lookup");
            operation.Property(o => o.CreatedAt).HasColumnName("created_at");

            // Idempotent on trip id plus operation id: this is the backstop for two consumers that raced, and the
            // lookup every later copy of a request makes first.
            operation.HasIndex(o => new { o.TripId, o.Kind, o.OperationId }).IsUnique().HasDatabaseName("ux_payment_operations_trip_kind_operation");
            operation.HasIndex(o => o.PaymentId).HasDatabaseName("ix_payment_operations_payment_id");
        });
    }
}
