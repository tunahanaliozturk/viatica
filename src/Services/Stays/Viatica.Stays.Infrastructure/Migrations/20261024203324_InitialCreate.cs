using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Viatica.Stays.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "stays");

        migrationBuilder.CreateTable(
            name: "holds",
            schema: "stays",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                item_id = table.Column<Guid>(type: "uuid", nullable: false),
                property_id = table.Column<Guid>(type: "uuid", nullable: false),
                room_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                check_in = table.Column<DateOnly>(type: "date", nullable: false),
                check_out = table.Column<DateOnly>(type: "date", nullable: false),
                quantity = table.Column<int>(type: "integer", nullable: false),
                total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_holds", x => x.id);
                table.CheckConstraint("ck_holds_shape", "quantity > 0 and check_out > check_in");
            });

        migrationBuilder.CreateTable(
            name: "inbox_messages",
            schema: "stays",
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
            schema: "stays",
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
            name: "properties",
            schema: "stays",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                partner_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                address_line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                postal_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                latitude = table.Column<double>(type: "double precision", nullable: false),
                longitude = table.Column<double>(type: "double precision", nullable: false),
                star_rating = table.Column<int>(type: "integer", nullable: false),
                amenities = table.Column<string[]>(type: "text[]", nullable: false),
                check_in_from = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                check_out_until = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                free_cancellation_hours = table.Column<int>(type: "integer", nullable: false),
                penalty_after_free_cancellation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                listing_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_properties", x => x.id);
                table.CheckConstraint("ck_properties_star_rating", "star_rating between 0 and 5");
            });

        migrationBuilder.CreateTable(
            name: "room_types",
            schema: "stays",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                property_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                max_occupancy = table.Column<int>(type: "integer", nullable: false),
                beds = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                amenities = table.Column<string[]>(type: "text[]", nullable: false),
                base_rate = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_room_types", x => x.id);
                table.CheckConstraint("ck_room_types_occupancy", "max_occupancy > 0");
                table.ForeignKey(
                    name: "FK_room_types_properties_property_id",
                    column: x => x.property_id,
                    principalSchema: "stays",
                    principalTable: "properties",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "room_nights",
            schema: "stays",
            columns: table => new
            {
                room_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                night = table.Column<DateOnly>(type: "date", nullable: false),
                allotment = table.Column<int>(type: "integer", nullable: false),
                sold = table.Column<int>(type: "integer", nullable: false),
                held = table.Column<int>(type: "integer", nullable: false),
                rate = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_room_nights", x => new { x.room_type_id, x.night });
                table.CheckConstraint("ck_room_nights_capacity", "allotment >= 0 and sold >= 0 and held >= 0 and sold + held <= allotment");
                table.ForeignKey(
                    name: "FK_room_nights_room_types_room_type_id",
                    column: x => x.room_type_id,
                    principalSchema: "stays",
                    principalTable: "room_types",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "room_type_inventory",
            schema: "stays",
            columns: table => new
            {
                room_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                availability_version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_room_type_inventory", x => x.room_type_id);
                table.ForeignKey(
                    name: "FK_room_type_inventory_room_types_room_type_id",
                    column: x => x.room_type_id,
                    principalSchema: "stays",
                    principalTable: "room_types",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_holds_due",
            schema: "stays",
            table: "holds",
            column: "expires_at",
            filter: "status = 'Held'");

        migrationBuilder.CreateIndex(
            name: "ix_holds_trip_id",
            schema: "stays",
            table: "holds",
            column: "trip_id");

        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_partition_key_sequence",
            schema: "stays",
            table: "outbox_messages",
            columns: new[] { "partition_key", "sequence" });

        migrationBuilder.CreateIndex(
            name: "ix_properties_partner_id",
            schema: "stays",
            table: "properties",
            column: "partner_id");

        migrationBuilder.CreateIndex(
            name: "ix_properties_status_name",
            schema: "stays",
            table: "properties",
            columns: new[] { "status", "name", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_room_types_property_id",
            schema: "stays",
            table: "room_types",
            column: "property_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "holds",
            schema: "stays");

        migrationBuilder.DropTable(
            name: "inbox_messages",
            schema: "stays");

        migrationBuilder.DropTable(
            name: "outbox_messages",
            schema: "stays");

        migrationBuilder.DropTable(
            name: "room_nights",
            schema: "stays");

        migrationBuilder.DropTable(
            name: "room_type_inventory",
            schema: "stays");

        migrationBuilder.DropTable(
            name: "room_types",
            schema: "stays");

        migrationBuilder.DropTable(
            name: "properties",
            schema: "stays");
    }
}
