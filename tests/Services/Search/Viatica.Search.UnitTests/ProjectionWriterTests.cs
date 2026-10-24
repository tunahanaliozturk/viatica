using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Time.Testing;
using Viatica.Contracts;
using Viatica.Search.Application.Projection;
using Viatica.Search.Infrastructure.Indexing;

namespace Viatica.Search.UnitTests;

public sealed class ProjectionWriterTests
{
    private static readonly Guid Property = Guid.Parse("0192f0c4-0000-7000-8000-000000000001");
    private static readonly Guid Room = Guid.Parse("0192f0c4-0000-7000-8000-0000000000a1");
    private static readonly DateOnly Night = Events.Today.AddDays(10);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Events.Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    private ProjectionEvent At(IIntegrationEvent message) => new(Guid.NewGuid(), _clock.GetUtcNow(), message);

    [Fact]
    public async Task One_batch_with_many_events_for_a_document_is_read_once_and_written_once()
    {
        var stays = new ScriptedStore<StayDocument>();
        var writer = new ProjectionWriter(stays, new ScriptedStore<CarLocationDocument>(), _clock, measureLag: false);

        IReadOnlyList<ProjectionOutcome> outcomes = await writer.ApplyAsync(
        [
            At(Events.Listing(Property, 1, roomTypes: [Room])),
            At(Events.Nights(Property, Room, 1, Night, 3, 2, 100m)),
            At(Events.Nights(Property, Room, 2, Night, 3, 1, 110m)),
            At(Events.Nights(Property, Room, 1, Night, 3, 9, 1m)),
        ], CancellationToken.None);

        outcomes.ShouldBe([ProjectionOutcome.Applied, ProjectionOutcome.Applied, ProjectionOutcome.Applied, ProjectionOutcome.Dropped]);
        stays.Reads.ShouldBe(1);
        stays.Writes.ShouldBe(1);
        stays.Documents[Property.ToString()].Rooms.Single().State.Available.ShouldAllBe(static a => a == 1);
    }

    [Fact]
    public async Task A_batch_with_nothing_new_writes_nothing()
    {
        var stays = new ScriptedStore<StayDocument>();
        var writer = new ProjectionWriter(stays, new ScriptedStore<CarLocationDocument>(), _clock, measureLag: false);
        ProjectionEvent listing = At(Events.Listing(Property, 3, roomTypes: [Room]));
        await writer.ApplyAsync([listing], CancellationToken.None);
        int writes = stays.Writes;

        IReadOnlyList<ProjectionOutcome> outcomes = await writer.ApplyAsync([listing, At(Events.Listing(Property, 2))], CancellationToken.None);

        outcomes.ShouldBe([ProjectionOutcome.Dropped, ProjectionOutcome.Dropped]);
        stays.Writes.ShouldBe(writes);
    }

    [Fact]
    public async Task A_document_changed_by_someone_else_in_between_is_read_again_and_the_events_replayed_on_top()
    {
        var stays = new ScriptedStore<StayDocument>();
        var writer = new ProjectionWriter(stays, new ScriptedStore<CarLocationDocument>(), _clock, measureLag: false);
        await writer.ApplyAsync([At(Events.Listing(Property, 1, roomTypes: [Room]))], CancellationToken.None);

        // Another writer (a rebuild finishing, say) lands availability for night 0 between our read and our write.
        stays.BeforeNextWrite = () =>
        {
            var other = new ProjectionWriter(new PlainView<StayDocument>(stays), new ScriptedStore<CarLocationDocument>(), _clock, measureLag: false);
            other.ApplyAsync([At(Events.Nights(Property, Room, 5, Night, 1, 4, 100m))], CancellationToken.None).GetAwaiter().GetResult();
        };

        await writer.ApplyAsync([At(Events.Nights(Property, Room, 6, Night.AddDays(1), 1, 2, 120m))], CancellationToken.None);

        stays.Conflicts.ShouldBe(1);
        RoomDocument room = stays.Documents[Property.ToString()].Rooms.Single();
        room.Open["1"].ShouldBe([new DayRange(Night.DayNumber, Night.DayNumber + 1)]);
    }

    [Fact]
    public async Task Events_that_are_not_listings_or_availability_are_refused()
    {
        var writer = new ProjectionWriter(new ScriptedStore<StayDocument>(), new ScriptedStore<CarLocationDocument>(), _clock, measureLag: false);

        await Should.ThrowAsync<ArgumentException>(() =>
            writer.ApplyAsync([At(new StayHoldCommitted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))], CancellationToken.None));
    }

    [Fact]
    public async Task A_document_that_never_stops_changing_ends_the_batch_in_an_error()
    {
        var stays = new ScriptedStore<StayDocument> { AlwaysConflict = true };
        var writer = new ProjectionWriter(stays, new ScriptedStore<CarLocationDocument>(), _clock, measureLag: false);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            writer.ApplyAsync([At(Events.Listing(Property, 1))], CancellationToken.None));
    }

    /// <summary>
    /// A store with the same optimistic-concurrency contract as the index, held in memory so a test can slip a
    /// competing write in between a read and a write. The index itself is covered by the integration tests.
    /// </summary>
    private sealed class ScriptedStore<TDocument> : IDocumentStore<TDocument>
        where TDocument : class
    {
        private long _sequence;
        private readonly Dictionary<string, (TDocument Document, long Seq)> _stored = [];

        public Action? BeforeNextWrite { get; set; }

        public bool AlwaysConflict { get; init; }

        public int Reads { get; private set; }

        public int Writes { get; private set; }

        public int Conflicts { get; private set; }

        public Dictionary<string, TDocument> Documents => _stored.ToDictionary(static p => p.Key, static p => p.Value.Document);

        public Task<IReadOnlyDictionary<string, StoredDocument<TDocument>>> GetAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyDictionary<string, StoredDocument<TDocument>>>(
                ids.Where(_stored.ContainsKey).ToDictionary(id => id, id => new StoredDocument<TDocument>(Clone(_stored[id].Document), _stored[id].Seq, 1)));
        }

        public Task<IReadOnlyList<string>> WriteAsync(IReadOnlyList<DocumentWrite<TDocument>> writes, CancellationToken cancellationToken)
        {
            Action? hook = BeforeNextWrite;
            BeforeNextWrite = null;
            hook?.Invoke();

            List<string> conflicts = [];
            foreach (DocumentWrite<TDocument> write in writes)
            {
                bool exists = _stored.TryGetValue(write.Id, out var current);
                bool clash = AlwaysConflict || (write.SeqNo is null ? exists : !exists || current.Seq != write.SeqNo);
                if (clash)
                {
                    conflicts.Add(write.Id);
                    Conflicts++;
                    continue;
                }

                Writes++;
                _stored[write.Id] = (Clone(write.Document), ++_sequence);
            }

            return Task.FromResult<IReadOnlyList<string>>(conflicts);
        }

        // The index hands back a fresh object each time; sharing one would let a projection edit the store in place.
        private static TDocument Clone(TDocument document)
        {
            var info = (JsonTypeInfo<TDocument>)IndexJsonContext.Default.GetTypeInfo(typeof(TDocument))!;
            return JsonSerializer.Deserialize(JsonSerializer.Serialize(document, info), info)!;
        }
    }

    /// <summary>The same store seen by a second writer.</summary>
    private sealed class PlainView<TDocument>(ScriptedStore<TDocument> inner) : IDocumentStore<TDocument>
        where TDocument : class
    {
        public Task<IReadOnlyDictionary<string, StoredDocument<TDocument>>> GetAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken) =>
            inner.GetAsync(ids, cancellationToken);

        public Task<IReadOnlyList<string>> WriteAsync(IReadOnlyList<DocumentWrite<TDocument>> writes, CancellationToken cancellationToken) =>
            inner.WriteAsync(writes, cancellationToken);
    }
}
