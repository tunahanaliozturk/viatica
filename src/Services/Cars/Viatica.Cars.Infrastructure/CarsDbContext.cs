using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Viatica.Cars.Application;
using Viatica.Cars.Domain;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;

namespace Viatica.Cars.Infrastructure;

/// <summary>The Cars database: catalogue, inventory, holds, and the outbox and inbox the messaging library maps.</summary>
public sealed class CarsDbContext(DbContextOptions<CarsDbContext> options) : DbContext(options), ICarsDb
{
    public const string Schema = "cars";

    public DbSet<RentalLocation> Locations => Set<RentalLocation>();

    public DbSet<CarCategory> Categories => Set<CarCategory>();

    public DbSet<PartnerSettings> PartnerSettings => Set<PartnerSettings>();

    public DbSet<InventoryDay> InventoryDays => Set<InventoryDay>();

    public DbSet<CarHold> Holds => Set<CarHold>();

    public void Publish(IIntegrationEvent integrationEvent) => this.AddIntegrationEvent(integrationEvent);

    /// <summary>
    /// One row per category and location holds the last version handed out. The upsert locks the row until the
    /// transaction ends, which is what orders concurrent writers of the same stream by their commits.
    /// </summary>
    public async Task<long> NextAvailabilityVersionAsync(Guid locationId, Guid categoryId, CancellationToken cancellationToken)
    {
        // The alias is how SqlQuery finds the one scalar column.
        List<long> versions = await Database.SqlQuery<long>(
            $"""
            insert into cars.availability_streams (location_id, category_id, version)
            values ({locationId}, {categoryId}, 1)
            on conflict (location_id, category_id) do update set version = availability_streams.version + 1
            returning version as "Value"
            """).ToListAsync(cancellationToken);

        return versions.Single();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddMessaging();

        modelBuilder.Entity<RentalLocation>(location =>
        {
            location.ToTable("rental_locations");
            location.HasKey(l => l.Id);
            location.HasIndex(l => l.PartnerId);
            location.Property(l => l.Name).HasMaxLength(120);
            location.Property(l => l.AirportCode).HasMaxLength(3).IsFixedLength();
            location.Property(l => l.Status).HasConversion<string>().HasMaxLength(16);
            location.Property(l => l.Version).IsRowVersion();

            location.OwnsOne(l => l.Address, address =>
            {
                address.Property(a => a.Line1).HasColumnName("address_line1").HasMaxLength(200);
                address.Property(a => a.City).HasColumnName("address_city").HasMaxLength(100);
                address.Property(a => a.Region).HasColumnName("address_region").HasMaxLength(100);
                address.Property(a => a.PostalCode).HasColumnName("address_postal_code").HasMaxLength(20);
                address.Property(a => a.CountryCode).HasColumnName("address_country_code").HasMaxLength(2).IsFixedLength();
            });
            location.Navigation(l => l.Address).IsRequired();

            location.OwnsOne(l => l.Position, position =>
            {
                position.Property(p => p.Latitude).HasColumnName("latitude");
                position.Property(p => p.Longitude).HasColumnName("longitude");
            });
            location.Navigation(l => l.Position).IsRequired();

            location.OwnsOne(l => l.Cancellation, policy =>
            {
                policy.Property(p => p.FreeUntilHoursBeforeStart).HasColumnName("cancellation_free_hours_before_start");
                policy.Property(p => p.PenaltyAfter).HasColumnName("cancellation_penalty_after").HasConversion<string>().HasMaxLength(16);
            });
            location.Navigation(l => l.Cancellation).IsRequired();

            // In the location's own row, as jsonb, so a change to the hours is a change to the row and moves its
            // version (the ETag). A table of its own would leave the parent row, and the ETag, untouched. The
            // list is mapped through its backing field because the entity exposes it read-only.
            location.Ignore(l => l.OpeningHours);
            location.Property<List<OpeningWindow>>("_openingHours")
                .HasColumnName("opening_hours")
                .HasColumnType("jsonb")
                .HasConversion(
                    static hours => JsonSerializer.Serialize(hours, CarsStorageJson.Default.ListOpeningWindow),
                    static json => JsonSerializer.Deserialize(json, CarsStorageJson.Default.ListOpeningWindow) ?? new List<OpeningWindow>(),
                    new ValueComparer<List<OpeningWindow>>(
                        static (left, right) => left!.SequenceEqual(right!),
                        static hours => hours.Aggregate(0, static (hash, window) => HashCode.Combine(hash, window)),
                        static hours => new List<OpeningWindow>(hours)));
        });

        modelBuilder.Entity<CarCategory>(category =>
        {
            category.ToTable("car_categories");
            category.HasKey(c => c.Id);
            category.HasIndex(c => c.PartnerId);
            category.Property(c => c.Acriss).HasMaxLength(4).IsFixedLength();
            category.Property(c => c.Name).HasMaxLength(120);
            category.Property(c => c.Transmission).HasConversion<string>().HasMaxLength(16);
            category.Property(c => c.Fuel).HasConversion<string>().HasMaxLength(16);
            category.Property(c => c.ImageUrl).HasMaxLength(2048);
            category.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            category.Property(c => c.Version).IsRowVersion();
        });

        modelBuilder.Entity<PartnerSettings>(settings =>
        {
            settings.ToTable("partner_settings");
            settings.HasKey(s => s.PartnerId);
            settings.Property(s => s.OneWayFeeAmount).HasColumnName("one_way_fee").HasPrecision(12, 2);
            settings.Property(s => s.YoungDriverSurchargePerDayAmount).HasColumnName("young_driver_surcharge_per_day").HasPrecision(12, 2);
            settings.Property(s => s.Version).IsRowVersion();
            settings.Ignore(s => s.OneWayFee);
            settings.Ignore(s => s.YoungDriverSurchargePerDay);
            settings.ToTable(table => table.HasCheckConstraint("ck_partner_settings_fees", "one_way_fee >= 0 and young_driver_surcharge_per_day >= 0"));
        });

        modelBuilder.Entity<InventoryDay>(day =>
        {
            day.ToTable("car_inventory_days");
            day.HasKey(d => new { d.LocationId, d.CategoryId, d.Date });
            day.Property(d => d.Rate).HasPrecision(12, 2);
            day.Ignore(d => d.Available);
            day.HasOne<RentalLocation>().WithMany().HasForeignKey(d => d.LocationId).OnDelete(DeleteBehavior.Restrict);
            day.HasOne<CarCategory>().WithMany().HasForeignKey(d => d.CategoryId).OnDelete(DeleteBehavior.Restrict);
            // The invariant, held by the database as well as by the conditional UPDATEs: no day ever sells or holds
            // more cars than exist, whatever code or person writes to the table.
            day.ToTable(table =>
            {
                table.HasCheckConstraint("ck_car_inventory_days_non_negative", "allotment >= 0 and sold >= 0 and held >= 0 and rate >= 0");
                table.HasCheckConstraint("ck_car_inventory_days_not_oversold", "sold + held <= allotment");
            });
        });

        modelBuilder.Entity<CarHold>(hold =>
        {
            hold.ToTable("car_holds");
            hold.HasKey(h => h.Id);
            hold.HasIndex(h => h.TripId);
            // The sweeper looks only at live holds, and most rows are not.
            hold.HasIndex(h => h.ExpiresAt).HasFilter("status = 'Held'").HasDatabaseName("ix_car_holds_live_expiry");
            hold.Property(h => h.Status).HasConversion<string>().HasMaxLength(16);
            hold.Property(h => h.Total).HasPrecision(12, 2);
            hold.Property(h => h.Version).IsRowVersion();
            hold.Ignore(h => h.LastDay);
            // timestamptz stores UTC only, and these two carry the location's own offset, which the events must
            // repeat exactly. ISO 8601 text keeps the offset; nothing queries these columns.
            hold.Property(h => h.PickUpAt).HasConversion(Iso8601Text).HasColumnType("text");
            hold.Property(h => h.DropOffAt).HasConversion(Iso8601Text).HasColumnType("text");
            hold.ToTable(table => table.HasCheckConstraint("ck_car_holds_days", "days between 1 and 30"));
        });

        modelBuilder.Entity<AvailabilityStream>(stream =>
        {
            stream.ToTable("availability_streams");
            stream.HasKey(s => new { s.LocationId, s.CategoryId });
        });

        SnakeCaseColumns(modelBuilder);
    }

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, string> Iso8601Text =
        new(
            value => value.ToString("O", CultureInfo.InvariantCulture),
            text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    // Explicit snake_case, as the brief asks: no naming-convention package has an EF Core 11 build. Owned types name
    // their columns above, and the row version is Postgres's own xmin.
    private static void SnakeCaseColumns(ModelBuilder modelBuilder)
    {
        foreach (IMutableEntityType entity in modelBuilder.Model.GetEntityTypes().Where(static e => !e.IsOwned()))
        {
            if (entity.ClrType == typeof(OutboxMessage) || entity.ClrType == typeof(InboxMessage))
            {
                continue;
            }

            foreach (IMutableProperty property in entity.GetProperties())
            {
                if (!property.IsConcurrencyToken && property.GetColumnName() == property.Name)
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

/// <summary>The last availability version handed out for one category at one location.</summary>
internal sealed class AvailabilityStream
{
    public Guid LocationId { get; init; }

    public Guid CategoryId { get; init; }

    public long Version { get; init; }
}

/// <summary>How the opening hours are written into their jsonb column: camelCase, weekdays by name.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<OpeningWindow>))]
internal sealed partial class CarsStorageJson : JsonSerializerContext;
