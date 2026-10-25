using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Viatica.Payments.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261024234357_Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "payments");

        migrationBuilder.CreateTable(
            name: "inbox_messages",
            schema: "payments",
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
            schema: "payments",
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
            name: "payment_methods",
            schema: "payments",
            columns: table => new
            {
                token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                provider_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                brand = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                last4 = table.Column<string>(type: "character(4)", fixedLength: true, maxLength: 4, nullable: false),
                exp_month = table.Column<int>(type: "integer", nullable: false),
                exp_year = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_payment_methods", x => x.token);
            });

        migrationBuilder.CreateTable(
            name: "payment_operations",
            schema: "payments",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                amount = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                provider_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                resolved_by_lookup = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_payment_operations", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "payments",
            schema: "payments",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                authorized_amount = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                captured_amount = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                refunded_amount = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                provider_intent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                payment_method_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                decline_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_payments", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_partition_key_sequence",
            schema: "payments",
            table: "outbox_messages",
            columns: new[] { "partition_key", "sequence" });

        migrationBuilder.CreateIndex(
            name: "ix_payment_operations_payment_id",
            schema: "payments",
            table: "payment_operations",
            column: "payment_id");

        migrationBuilder.CreateIndex(
            name: "ux_payment_operations_trip_kind_operation",
            schema: "payments",
            table: "payment_operations",
            columns: new[] { "trip_id", "kind", "operation_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_payments_trip_id",
            schema: "payments",
            table: "payments",
            column: "trip_id",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "inbox_messages",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "outbox_messages",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "payment_methods",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "payment_operations",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "payments",
            schema: "payments");
    }
}
