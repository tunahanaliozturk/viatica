using Microsoft.EntityFrameworkCore;
using Npgsql;
using Viatica.Stays.Infrastructure;

namespace Viatica.Stays.IntegrationTests;

/// <summary>The migration against a real Postgres: it matches the model, and the database refuses an oversold night by itself.</summary>
public sealed class SchemaTests(StaysFixture stays) : IClassFixture<StaysFixture>
{
    [Fact]
    public async Task The_migrations_describe_the_model_exactly()
    {
        // The host applied them to an empty database before it started, so this is the real migration, not the model.
        bool pending = await stays.Host.InDbAsync(static db => Task.FromResult(db.Database.HasPendingModelChanges()));

        pending.ShouldBeFalse("add a migration for the model change");
    }

    [Fact]
    public async Task The_database_refuses_a_night_that_sells_more_than_it_has()
    {
        var scenario = await Scenario.CreateAsync(stays.Host, allotment: 5);

        PostgresException error = await Should.ThrowAsync<PostgresException>(() => stays.Host.InDbAsync(db => db.Database.ExecuteSqlAsync(
            $"update stays.room_nights set held = 6 where room_type_id = {scenario.RoomTypeId}")));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("ck_room_nights_capacity");
    }

    [Fact]
    public async Task The_outbox_is_drained_once_the_events_are_on_the_topic()
    {
        await Scenario.CreateAsync(stays.Host);

        // The relay deletes a row when Kafka acknowledges it, so a quiet service has an empty outbox (invariant 5).
        await Viatica.Testing.Eventually.MatchesAsync(
            () => stays.Host.InDbAsync(static db => db.Set<Viatica.Kernel.Messaging.OutboxMessage>().CountAsync()),
            static count => count == 0);
    }

    [Fact]
    public void The_schema_name_is_the_one_the_roles_expect()
    {
        StaysDbContext.Schema.ShouldBe("stays");
    }
}
