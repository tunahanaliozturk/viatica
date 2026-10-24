using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Viatica.Cars.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "cars");

        migrationBuilder.CreateTable(
            name: "availability_streams",
            schema: "cars",
            columns: table => new
            {
                location_id = table.Column<Guid>(type: "uuid", nullable: false),
                category_id = table.Column<Guid>(type: "uuid", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_availability_streams", x => new { x.location_id, x.category_id });
            });

        migrationBuilder.CreateTable(
            name: "car_categories",
            schema: "cars",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                partner_id = table.Column<Guid>(type: "uuid", nullable: false),
                acriss = table.Column<string>(type: "character(4)", fixedLength: true, maxLength: 4, nullable: false),
                name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                seats = table.Column<int>(type: "integer", nullable: false),
                transmission = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                fuel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                bags = table.Column<int>(type: "integer", nullable: false),
                minimum_driver_age = table.Column<int>(type: "integer", nullable: false),
                image_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_car_categories", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "car_holds",
            schema: "cars",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                item_id = table.Column<Guid>(type: "uuid", nullable: false),
                location_id = table.Column<Guid>(type: "uuid", nullable: false),
                category_id = table.Column<Guid>(type: "uuid", nullable: false),
                drop_off_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                pick_up_at = table.Column<string>(type: "text", nullable: false),
                drop_off_at = table.Column<string>(type: "text", nullable: false),
                first_day = table.Column<DateOnly>(type: "date", nullable: false),
                days = table.Column<int>(type: "integer", nullable: false),
                total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_car_holds", x => x.id);
                table.CheckConstraint("ck_car_holds_days", "days between 1 and 30");
            });

        migrationBuilder.CreateTable(
            name: "inbox_messages",
            schema: "cars",
            columns: table => new
            {
                consumer_group = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inbox_messages", x => new { x.consumer_group, x.event_id });
            });

        migrationBuilder.CreateTable(
            name: "outbox_messages",
            schema: "cars",
            columns: table => new
            {
                sequence = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                id = table.Column<Guid>(type: "uuid", nullable: false),
                type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                partition_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                payload = table.Column<string>(type: "text", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                traceparent = table.Column<string>(type: "character varying(55)", maxLength: 55, nullable: true),
                attempts = table.Column<int>(type: "integer", nullable: false),
                last_error = table.Column<string>(type: "text", nullable: true),
                next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outbox_messages", x => x.sequence);
            });

        migrationBuilder.CreateTable(
            name: "partner_settings",
            schema: "cars",
            columns: table => new
            {
                partner_id = table.Column<Guid>(type: "uuid", nullable: false),
                one_way_fee = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                young_driver_surcharge_per_day = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_partner_settings", x => x.partner_id);
                table.CheckConstraint("ck_partner_settings_fees", "one_way_fee >= 0 and young_driver_surcharge_per_day >= 0");
            });

        migrationBuilder.CreateTable(
            name: "rental_locations",
            schema: "cars",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                partner_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                address_line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                address_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                address_postal_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                address_country_code = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                address_region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                latitude = table.Column<double>(type: "double precision", nullable: false),
                longitude = table.Column<double>(type: "double precision", nullable: false),
                airport_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                cancellation_free_hours_before_start = table.Column<int>(type: "integer", nullable: false),
                cancellation_penalty_after = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                listing_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                opening_hours = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_rental_locations", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "car_inventory_days",
            schema: "cars",
            columns: table => new
            {
                location_id = table.Column<Guid>(type: "uuid", nullable: false),
                category_id = table.Column<Guid>(type: "uuid", nullable: false),
                date = table.Column<DateOnly>(type: "date", nullable: false),
                allotment = table.Column<int>(type: "integer", nullable: false),
                sold = table.Column<int>(type: "integer", nullable: false),
                held = table.Column<int>(type: "integer", nullable: false),
                rate = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_car_inventory_days", x => new { x.location_id, x.category_id, x.date });
                table.CheckConstraint("ck_car_inventory_days_non_negative", "allotment >= 0 and sold >= 0 and held >= 0 and rate >= 0");
                table.CheckConstraint("ck_car_inventory_days_not_oversold", "sold + held <= allotment");
                table.ForeignKey(
                    name: "FK_car_inventory_days_car_categories_category_id",
                    column: x => x.category_id,
                    principalSchema: "cars",
                    principalTable: "car_categories",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_car_inventory_days_rental_locations_location_id",
                    column: x => x.location_id,
                    principalSchema: "cars",
                    principalTable: "rental_locations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_car_categories_partner_id",
            schema: "cars",
            table: "car_categories",
            column: "partner_id");

        migrationBuilder.CreateIndex(
            name: "IX_car_holds_trip_id",
            schema: "cars",
            table: "car_holds",
            column: "trip_id");

        migrationBuilder.CreateIndex(
            name: "ix_car_holds_live_expiry",
            schema: "cars",
            table: "car_holds",
            column: "expires_at",
            filter: "status = 'Held'");

        migrationBuilder.CreateIndex(
            name: "IX_car_inventory_days_category_id",
            schema: "cars",
            table: "car_inventory_days",
            column: "category_id");

        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_partition_key_sequence",
            schema: "cars",
            table: "outbox_messages",
            columns: new[] { "partition_key", "sequence" });

        migrationBuilder.CreateIndex(
            name: "IX_rental_locations_partner_id",
            schema: "cars",
            table: "rental_locations",
            column: "partner_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "availability_streams",
            schema: "cars");

        migrationBuilder.DropTable(
            name: "car_holds",
            schema: "cars");

        migrationBuilder.DropTable(
            name: "car_inventory_days",
            schema: "cars");

        migrationBuilder.DropTable(
            name: "inbox_messages",
            schema: "cars");

        migrationBuilder.DropTable(
            name: "outbox_messages",
            schema: "cars");

        migrationBuilder.DropTable(
            name: "partner_settings",
            schema: "cars");

        migrationBuilder.DropTable(
            name: "car_categories",
            schema: "cars");

        migrationBuilder.DropTable(
            name: "rental_locations",
            schema: "cars");
    }
}
