using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Infrastructure;

/// <summary>
/// Table and column names are spelled out in snake_case. The naming-convention package has no EF Core 11 build, and
/// the names are part of what a DBA reads, so they are written down rather than derived.
/// </summary>
internal static class StaysModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StayProperty>(PropertyTable);
        modelBuilder.Entity<RoomType>(RoomTypeTable);
        modelBuilder.Entity<RoomTypeInventory>(Inventory);
        modelBuilder.Entity<RoomNight>(Night);
        modelBuilder.Entity<Hold>(HoldTable);
    }

    private static void PropertyTable(EntityTypeBuilder<StayProperty> e)
    {
        e.ToTable("properties");
        e.HasKey(p => p.Id);
        e.Ignore(p => p.Details);
        e.Ignore(p => p.IsPublished);

        e.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        e.Property(p => p.PartnerId).HasColumnName("partner_id");
        e.Property(p => p.Name).HasColumnName("name").HasMaxLength(200);
        e.Property(p => p.Description).HasColumnName("description").HasMaxLength(4000);
        e.Property(p => p.AddressLine1).HasColumnName("address_line1").HasMaxLength(200);
        e.Property(p => p.City).HasColumnName("city").HasMaxLength(100);
        e.Property(p => p.Region).HasColumnName("region").HasMaxLength(100);
        e.Property(p => p.PostalCode).HasColumnName("postal_code").HasMaxLength(20);
        e.Property(p => p.CountryCode).HasColumnName("country_code").HasMaxLength(2);
        e.Property(p => p.Latitude).HasColumnName("latitude");
        e.Property(p => p.Longitude).HasColumnName("longitude");
        e.Property(p => p.StarRating).HasColumnName("star_rating");
        e.Property(p => p.Amenities).HasColumnName("amenities");
        e.Property(p => p.CheckInFrom).HasColumnName("check_in_from");
        e.Property(p => p.CheckOutUntil).HasColumnName("check_out_until");
        e.Property(p => p.FreeCancellationHours).HasColumnName("free_cancellation_hours");
        e.Property(p => p.PenaltyAfterFreeCancellation).HasColumnName("penalty_after_free_cancellation").HasConversion<string>().HasMaxLength(20);
        e.Property(p => p.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        e.Property(p => p.ListingVersion).HasColumnName("listing_version");
        e.Property(p => p.CreatedAt).HasColumnName("created_at");
        e.Property(p => p.UpdatedAt).HasColumnName("updated_at");

        // xmin is the row version Postgres keeps anyway. EF Core compares it in the WHERE of every update, so a
        // writer that read an older row loses with a concurrency exception, which the API answers with 412.
        e.Property(p => p.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        e.HasIndex(p => p.PartnerId).HasDatabaseName("ix_properties_partner_id");
        e.HasIndex(p => new { p.Status, p.Name, p.Id }).HasDatabaseName("ix_properties_status_name");
        e.ToTable(t => t.HasCheckConstraint("ck_properties_star_rating", "star_rating between 0 and 5"));

        e.HasMany(p => p.RoomTypes).WithOne(r => r.Property).HasForeignKey(r => r.PropertyId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void RoomTypeTable(EntityTypeBuilder<RoomType> e)
    {
        e.ToTable("room_types");
        e.HasKey(r => r.Id);
        e.Ignore(r => r.Details);

        e.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        e.Property(r => r.PropertyId).HasColumnName("property_id");
        e.Property(r => r.Name).HasColumnName("name").HasMaxLength(100);
        e.Property(r => r.MaxOccupancy).HasColumnName("max_occupancy");
        e.Property(r => r.Beds).HasColumnName("beds").HasMaxLength(100);
        e.Property(r => r.Amenities).HasColumnName("amenities");
        e.Property(r => r.BaseRate).HasColumnName("base_rate").HasPrecision(12, 2);
        e.Property(r => r.CreatedAt).HasColumnName("created_at");
        e.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        e.Property(r => r.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        e.HasIndex(r => r.PropertyId).HasDatabaseName("ix_room_types_property_id");
        e.ToTable(t => t.HasCheckConstraint("ck_room_types_occupancy", "max_occupancy > 0"));
    }

    private static void Inventory(EntityTypeBuilder<RoomTypeInventory> e)
    {
        e.ToTable("room_type_inventory");
        e.HasKey(i => i.RoomTypeId);
        e.Property(i => i.RoomTypeId).HasColumnName("room_type_id").ValueGeneratedNever();
        e.Property(i => i.AvailabilityVersion).HasColumnName("availability_version");
        e.HasOne<RoomType>().WithOne().HasForeignKey<RoomTypeInventory>(i => i.RoomTypeId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void Night(EntityTypeBuilder<RoomNight> e)
    {
        e.ToTable("room_nights", t =>
            // The invariant, a second time: the statements that change these columns carry the rule in their WHERE,
            // and this constraint is what makes any statement that forgot to still fail instead of overselling.
            t.HasCheckConstraint("ck_room_nights_capacity", "allotment >= 0 and sold >= 0 and held >= 0 and sold + held <= allotment"));
        e.HasKey(n => new { n.RoomTypeId, n.Night });
        e.Ignore(n => n.Available);

        e.Property(n => n.RoomTypeId).HasColumnName("room_type_id");
        e.Property(n => n.Night).HasColumnName("night");
        e.Property(n => n.Allotment).HasColumnName("allotment");
        e.Property(n => n.Sold).HasColumnName("sold");
        e.Property(n => n.Held).HasColumnName("held");
        e.Property(n => n.Rate).HasColumnName("rate").HasPrecision(12, 2);

        e.HasOne<RoomType>().WithMany().HasForeignKey(n => n.RoomTypeId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void HoldTable(EntityTypeBuilder<Hold> e)
    {
        e.ToTable("holds", t => t.HasCheckConstraint("ck_holds_shape", "quantity > 0 and check_out > check_in"));
        e.HasKey(h => h.Id);
        e.Ignore(h => h.Dates);

        e.Property(h => h.Id).HasColumnName("id").ValueGeneratedNever();
        e.Property(h => h.TripId).HasColumnName("trip_id");
        e.Property(h => h.ItemId).HasColumnName("item_id");
        e.Property(h => h.PropertyId).HasColumnName("property_id");
        e.Property(h => h.RoomTypeId).HasColumnName("room_type_id");
        e.Property(h => h.CheckIn).HasColumnName("check_in");
        e.Property(h => h.CheckOut).HasColumnName("check_out");
        e.Property(h => h.Quantity).HasColumnName("quantity");
        e.Property(h => h.Total).HasColumnName("total").HasPrecision(12, 2);
        e.Property(h => h.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        e.Property(h => h.ExpiresAt).HasColumnName("expires_at");
        e.Property(h => h.CreatedAt).HasColumnName("created_at");
        e.Property(h => h.UpdatedAt).HasColumnName("updated_at");

        // The sweeper asks "which live holds are due" every few seconds. Only held rows can be due, and most rows are
        // not held for long, so a partial index stays small however many holds the table accumulates.
        e.HasIndex(h => h.ExpiresAt).HasDatabaseName("ix_holds_due").HasFilter("status = 'Held'");
        e.HasIndex(h => h.TripId).HasDatabaseName("ix_holds_trip_id");
    }
}
