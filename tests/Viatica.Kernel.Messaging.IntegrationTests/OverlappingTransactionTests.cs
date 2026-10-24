using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Viatica.Testing;

namespace Viatica.Kernel.Messaging.IntegrationTests;

/// <summary>
/// The outbox promises per key order. Identity values are handed out when a row is inserted, not when its
/// transaction commits, so two overlapping transactions can commit in the opposite order to their sequences.
/// </summary>
public sealed class OverlappingTransactionTests(PostgresDatabase database, KafkaFixture kafka) : IClassFixture<PostgresDatabase>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await TestService.EnsureSchemaAsync(database);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_later_event_of_a_key_is_not_published_before_an_earlier_one_whose_transaction_is_still_open()
    {
        string topic = await kafka.CreateTopicAsync("overlap", partitions: 1);
        string key = Guid.NewGuid().ToString();
        using IHost host = await TestService.StartAsync(database.ConnectionString, kafka, topic, consumerGroup: "overlap");
        using EventProbe probe = kafka.Probe(topic);

        // Transaction A writes sequence 1 for the key and stays open, as a request waiting on a slow call would.
        await using AsyncServiceScope scopeA = host.Services.CreateAsyncScope();
        TestDb dbA = scopeA.ServiceProvider.GetRequiredService<TestDb>();
        await using IDbContextTransaction transactionA = await dbA.Database.BeginTransactionAsync();
        dbA.AddIntegrationEvent(new ThingHappened(key, 1));
        await dbA.SaveChangesAsync();

        // Transaction B writes sequence 2 for the same key. It either commits at once (nothing orders it behind A)
        // or it waits for A's lock on the key.
        Task publishB = TestService.PublishAsync(host, new ThingHappened(key, 2));
        await Eventually.MatchesAsync(
            async () => publishB.IsCompleted || await WaitingOnAdvisoryLockAsync(),
            static settled => settled);

        // Room for many relay rounds. If B committed, the relay now sends sequence 2.
        await Should.ThrowAsync<TimeoutException>(probe.WaitForAsync(m => m.Key == key, TimeSpan.FromSeconds(1)));

        await transactionA.CommitAsync();
        await publishB;

        IReadOnlyList<ProbedMessage> messages = await probe.WaitForCountAsync(2, m => m.Key == key);
        messages.Select(static m => int.Parse(m.DataString("sequence")!, CultureInfo.InvariantCulture)).ShouldBe([1, 2]);
        await Eventually.MatchesAsync(() => TestService.AppliedAsync(host, key), static rows => rows.Count == 2);
        (await TestService.AppliedAsync(host, key)).Select(static a => a.Sequence).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Transactions_writing_the_same_keys_in_opposite_order_do_not_deadlock()
    {
        string topic = await kafka.CreateTopicAsync("deadlock", partitions: 1);
        string x = $"x-{Guid.NewGuid():N}";
        string y = $"y-{Guid.NewGuid():N}";
        using IHost host = await TestService.StartAsync(database.ConnectionString, kafka, topic, consumerGroup: "deadlock");

        // Each round, one transaction saves x then y and the other y then x, and both are open at the same time.
        for (int round = 0; round < 20; round++)
        {
            await Task.WhenAll(
                TestService.PublishAsync(host, new ThingHappened(x, round), new ThingHappened(y, round)),
                TestService.PublishAsync(host, new ThingHappened(y, round), new ThingHappened(x, round)));
        }

        await Eventually.MatchesAsync(() => TestService.AppliedAsync(host, x), static rows => rows.Count == 40);
    }

    private async Task<bool> WaitingOnAdvisoryLockAsync()
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select count(*) from pg_locks where locktype = 'advisory' and not granted";
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }
}
