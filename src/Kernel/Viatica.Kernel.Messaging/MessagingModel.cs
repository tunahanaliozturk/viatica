using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Viatica.Contracts;

namespace Viatica.Kernel.Messaging;

/// <summary>How a service's own <see cref="DbContext"/> takes the outbox and the inbox.</summary>
public static class MessagingModel
{
    /// <summary>
    /// Maps <see cref="OutboxMessage"/> and <see cref="InboxMessage"/> into the context's model, so they live in the
    /// service's own database and schema and the service's migrations create them. Call from
    /// <c>OnModelCreating</c>.
    /// </summary>
    public static ModelBuilder AddMessaging(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Column names are spelled out because the relay's locking query is raw SQL; only the table name and
        // schema are read from the model, so a service may still choose its own schema.
        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_messages");
            outbox.HasKey(m => m.Sequence);
            outbox.Property(m => m.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            outbox.Property(m => m.Id).HasColumnName("id");
            outbox.Property(m => m.Type).HasColumnName("type").HasMaxLength(200);
            outbox.Property(m => m.PartitionKey).HasColumnName("partition_key").HasMaxLength(200);
            outbox.Property(m => m.Payload).HasColumnName("payload");
            outbox.Property(m => m.OccurredAt).HasColumnName("occurred_at");
            outbox.Property(m => m.TraceParent).HasColumnName("traceparent").HasMaxLength(55);
            outbox.Property(m => m.Attempts).HasColumnName("attempts");
            outbox.Property(m => m.LastError).HasColumnName("last_error");
            outbox.Property(m => m.NextAttemptAt).HasColumnName("next_attempt_at");
            // Finds the oldest pending row of a key, which is what the relay locks to own that key.
            outbox.HasIndex(m => new { m.PartitionKey, m.Sequence });
        });

        modelBuilder.Entity<InboxMessage>(inbox =>
        {
            inbox.ToTable("inbox_messages");
            inbox.HasKey(m => new { m.ConsumerGroup, m.EventId });
            inbox.Property(m => m.ConsumerGroup).HasColumnName("consumer_group").HasMaxLength(200);
            inbox.Property(m => m.EventId).HasColumnName("event_id");
            inbox.Property(m => m.Type).HasColumnName("type").HasMaxLength(200);
            inbox.Property(m => m.ProcessedAt).HasColumnName("processed_at");
        });

        return modelBuilder;
    }

    /// <summary>
    /// Queues <paramref name="event"/> in the outbox. Nothing is sent and nothing is saved here: the row is written
    /// by the caller's next <c>SaveChanges</c>, in the same transaction as the state change, and the relay sends it
    /// after that commits.
    /// </summary>
    /// <remarks>
    /// The event types come from the application service provider rather than a constructor argument, so a pooled
    /// DbContext, whose constructor may take nothing but options, can publish too.
    /// </remarks>
    public static void AddIntegrationEvent(this DbContext db, IIntegrationEvent @event)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(@event);

        OutboxKeyLocks.EnsureOn(db);
        db.Set<OutboxMessage>().Add(OutboxMessage.For(@event, db.GetService<IntegrationEventTypes>()));
    }
}
