using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Viatica.Flights.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "flights");

        migrationBuilder.CreateTable(
            name: "flight_orders",
            schema: "flights",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                item_id = table.Column<Guid>(type: "uuid", nullable: false),
                airline_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                booking_reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                offer_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                total_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                ticketing_time_limit = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ticket_numbers = table.Column<string[]>(type: "text[]", nullable: false),
                ticketing_refusal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                refund_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                sealed_passengers = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_flight_orders", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "inbox_messages",
            schema: "flights",
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
            schema: "flights",
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

        migrationBuilder.CreateIndex(
            name: "ix_flight_orders_trip_id",
            schema: "flights",
            table: "flight_orders",
            column: "trip_id");

        migrationBuilder.CreateIndex(
            name: "ux_flight_orders_booking_reference",
            schema: "flights",
            table: "flight_orders",
            columns: new[] { "airline_code", "booking_reference" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_flight_orders_item_id",
            schema: "flights",
            table: "flight_orders",
            column: "item_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_partition_key_sequence",
            schema: "flights",
            table: "outbox_messages",
            columns: new[] { "partition_key", "sequence" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "flight_orders",
            schema: "flights");

        migrationBuilder.DropTable(
            name: "inbox_messages",
            schema: "flights");

        migrationBuilder.DropTable(
            name: "outbox_messages",
            schema: "flights");
    }
}
