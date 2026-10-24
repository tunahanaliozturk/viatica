using System.Globalization;
using System.Text.Json;
using Viatica.Contracts;
using Viatica.Search.Infrastructure.Indexing;
using Viatica.Search.Infrastructure.Messaging;
using Viatica.Testing;

namespace Viatica.Search.IntegrationTests;

/// <summary>
/// SRC-5: the projection is rebuilt from the topics into new indices that replace the old ones behind the aliases,
/// and search stays up while that happens.
/// </summary>
public sealed class RebuildTests(SearchApp app) : IClassFixture<SearchApp>, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    public async ValueTask InitializeAsync() => await app.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // A property keeps its room types when its listing is edited, so an edit reuses the specs it started with.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, RoomSpec[]> _rooms = new();

    private IIntegrationEvent[] Hotel(Guid id, int number, string? name = null, long listingVersion = 1)
    {
        RoomSpec[] rooms = _rooms.GetOrAdd(id, _ => [new RoomSpec("Standard", 2, 3, 80m + (number % 40)), new RoomSpec("Suite", 4, 2, 200m + (number % 40))]);
        IIntegrationEvent[] events = Seed.Property(id, name ?? $"Hotel {number:000}", "Lisbon", 1 + (number % 5), 38.70 + (number * 0.0005), -9.14, ["wifi"], rooms);
        return listingVersion == 1
            ? events
            : [Seed.Listing(id, listingVersion, name ?? $"Hotel {number:000}", "Lisbon", 1 + (number % 5), 38.70, -9.14, ["wifi"], rooms)];
    }

    [Fact]
    public async Task A_rebuild_replaces_the_indices_from_the_topics_while_queries_keep_succeeding()
    {
        // 1. A live index built the ordinary way: 150 properties and 6 rental locations through Kafka.
        Guid[] ids = [.. Enumerable.Range(0, 150).Select(static _ => Guid.CreateVersion7())];
        for (int i = 0; i < ids.Length; i++)
        {
            await app.PublishAsync(Hotel(ids[i], i));
        }

        for (int i = 0; i < 6; i++)
        {
            await app.PublishAsync(Seed.Location(Guid.NewGuid(), $"Location {i}", "Lisbon", 38.77, -9.13, Seed.CarPartner, null, new CarSpec("ECMR", "Economy", 4, Transmission.Manual, 2, 30m + i)));
        }

        await Eventually.MatchesAsync(async () => await CountAsync("stays"), static count => count == 150, Patience);
        await Eventually.MatchesAsync(async () => await CountAsync("cars"), static count => count == 6, Patience);

        IReadOnlyList<string> before = await app.Admin.BehindAsync("stays", CancellationToken.None);
        before.Count.ShouldBe(1);

        // Drift the rebuild must repair: a document that vanished from the live index, which no live event will ever
        // bring back. Only a replay of the topic can.
        using (JsonDocument _ = await app.Rest.SendAsync(OpenSearch.Net.HttpMethod.DELETE, $"{before[0]}/_doc/{ids[100]}", null, CancellationToken.None))
        {
        }

        (await app.StayDocumentAsync(ids[100])).ShouldBeNull();

        // 2. Queries of every kind, all the time, from four loops, while the rebuild runs.
        using var stop = new CancellationTokenSource();
        var failures = new List<string>();
        int answered = 0;
        Task[] load = [.. Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            string stay = $"checkIn={SearchApp.Base.AddDays(3):yyyy-MM-dd}&checkOut={SearchApp.Base.AddDays(6):yyyy-MM-dd}";
            string car = $"lat=38.77&lon=-9.13&pickUpAt={SearchApp.Base.AddDays(3):yyyy-MM-dd}T10:00:00Z&dropOffAt={SearchApp.Base.AddDays(6):yyyy-MM-dd}T10:00:00Z";
            string[] urls =
            [
                $"/stays/search?destination=lisbon&{stay}&sort=price&$top=20",
                $"/stays/search?destination=lisbon&{stay}&sort=rating&stars=3,4,5&$top=10",
                $"/cars/search?{car}",
                "/autocomplete?q=lis",
            ];

            for (int n = worker; !stop.IsCancellationRequested; n++)
            {
                (int status, JsonElement body) = await app.GetAsync(urls[n % urls.Length]);
                if (status != 200)
                {
                    lock (failures)
                    {
                        failures.Add($"{urls[n % urls.Length]} -> {status} {body}");
                    }
                }

                Interlocked.Increment(ref answered);
            }
        }))];

        // 3. Events keep arriving during the rebuild: ten renamed listings and forty new properties.
        Guid[] arriving = [.. Enumerable.Range(150, 40).Select(static _ => Guid.CreateVersion7())];
        Task publisher = Task.Run(async () =>
        {
            await Task.Delay(300);
            for (int i = 0; i < 10; i++)
            {
                await app.PublishAsync(Hotel(ids[i], i, $"Renamed {i:000}", listingVersion: 2));
            }

            for (int i = 0; i < arriving.Length; i++)
            {
                await app.PublishAsync(Hotel(arriving[i], 150 + i));
                await Task.Delay(20);
            }
        });

        RebuildReport report = await app.Service<RebuildRunner>().RunAsync(CancellationToken.None);
        await publisher;

        // Let the loops run on the new indices for a moment too, then stop them.
        await Task.Delay(500);
        await stop.CancelAsync();
        await Task.WhenAll(load);

        failures.ShouldBeEmpty();
        answered.ShouldBeGreaterThan(50);

        // 4. The aliases point at the new indices, the old ones are gone, and nothing is missing.
        (await app.Admin.BehindAsync("stays", CancellationToken.None)).ShouldBe([report.NewIndices[IndexKind.Stays]]);
        (await app.Admin.BehindAsync("cars", CancellationToken.None)).ShouldBe([report.NewIndices[IndexKind.Cars]]);
        report.RemovedIndices.ShouldContain(before[0]);
        (await app.Rest.ExistsAsync(before[0], CancellationToken.None)).ShouldBeFalse();
        report.EventsRead.ShouldBeGreaterThanOrEqualTo(150 * 3);

        await Eventually.MatchesAsync(async () => await CountAsync("stays"), static count => count == 190, Patience);
        (await app.StayDocumentAsync(ids[100])).ShouldNotBeNull();
        (await app.StayDocumentAsync(ids[3]))!.Value.GetProperty("name").GetString().ShouldBe("Renamed 003");
        (await app.StayDocumentAsync(arriving[^1])).ShouldNotBeNull();
        (await CountAsync("cars")).ShouldBe(6);

        // 5. The live consumers write to the alias, so they follow it to the new index without a restart.
        Guid after = Guid.CreateVersion7();
        await app.PublishAsync(Hotel(after, 999));
        await Eventually.MatchesAsync(async () => await app.StayDocumentAsync(after), static d => d is not null, Patience);

        // 6. Rebuilding again is as safe as the first time.
        RebuildReport again = await app.Service<RebuildRunner>().RunAsync(CancellationToken.None);
        again.NewIndices[IndexKind.Stays].ShouldNotBe(report.NewIndices[IndexKind.Stays]);
        await app.RefreshAsync();
        (await CountAsync("stays")).ShouldBe(191);
    }

    private async Task<long> CountAsync(string alias)
    {
        using JsonDocument refresh = await app.Rest.SendAsync(OpenSearch.Net.HttpMethod.POST, $"{alias}/_refresh", null, CancellationToken.None);
        using JsonDocument answer = await app.Rest.SendAsync(OpenSearch.Net.HttpMethod.GET, $"{alias}/_count", null, CancellationToken.None);
        return answer.RootElement.GetProperty("count").GetInt64();
    }
}
