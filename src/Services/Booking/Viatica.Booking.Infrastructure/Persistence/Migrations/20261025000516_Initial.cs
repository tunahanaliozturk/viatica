using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Viatica.Booking.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "booking");

        migrationBuilder.CreateTable(
            name: "inbox_messages",
            schema: "booking",
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
            schema: "booking",
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
            name: "trips",
            schema: "booking",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                traveller_id = table.Column<Guid>(type: "uuid", nullable: true),
                access_key_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                revision = table.Column<int>(type: "integer", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                checkout_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                checkout_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                payment_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                payment_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                sealed_traveller_data = table.Column<string>(type: "text", nullable: true),
                failure = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                deadline_margin = table.Column<TimeSpan>(type: "interval", nullable: false),
                assumed_hold_lifetime = table.Column<TimeSpan>(type: "interval", nullable: false),
                resend_after = table.Column<TimeSpan>(type: "interval", nullable: false),
                compensation_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                next_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_trips", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "trip_items",
            schema: "booking",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                awaiting = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                reference = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                expected_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                quantity = table.Column<int>(type: "integer", nullable: false),
                driver_age = table.Column<int>(type: "integer", nullable: true),
                quote_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: true),
                total_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                partner_id = table.Column<Guid>(type: "uuid", nullable: true),
                starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                units = table.Column<int>(type: "integer", nullable: false),
                free_cancellation_hours = table.Column<int>(type: "integer", nullable: true),
                penalty = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                refusal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                cancel_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                cancel_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                cancel_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                refund_id = table.Column<Guid>(type: "uuid", nullable: true),
                refund_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                refund_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_trip_items", x => x.id);
                table.ForeignKey(
                    name: "FK_trip_items_trips_trip_id",
                    column: x => x.trip_id,
                    principalSchema: "booking",
                    principalTable: "trips",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_partition_key_sequence",
            schema: "booking",
            table: "outbox_messages",
            columns: new[] { "partition_key", "sequence" });

        migrationBuilder.CreateIndex(
            name: "ix_trip_items_partner_id_added_at",
            schema: "booking",
            table: "trip_items",
            columns: new[] { "partner_id", "added_at" },
            filter: "partner_id is not null");

        migrationBuilder.CreateIndex(
            name: "ix_trip_items_trip_id",
            schema: "booking",
            table: "trip_items",
            column: "trip_id");

        migrationBuilder.CreateIndex(
            name: "ux_trip_items_hold_id",
            schema: "booking",
            table: "trip_items",
            column: "hold_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_trips_next_check_at",
            schema: "booking",
            table: "trips",
            column: "next_check_at",
            filter: "next_check_at is not null");

        migrationBuilder.CreateIndex(
            name: "ix_trips_traveller_id_created_at",
            schema: "booking",
            table: "trips",
            columns: new[] { "traveller_id", "created_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "inbox_messages",
            schema: "booking");

        migrationBuilder.DropTable(
            name: "outbox_messages",
            schema: "booking");

        migrationBuilder.DropTable(
            name: "trip_items",
            schema: "booking");

        migrationBuilder.DropTable(
            name: "trips",
            schema: "booking");
    }
}
