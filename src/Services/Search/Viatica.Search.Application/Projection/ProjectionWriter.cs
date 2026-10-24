using Viatica.Contracts;

namespace Viatica.Search.Application.Projection;

/// <summary>A document as the store holds it, with the position that makes a conditional write possible.</summary>
public sealed record StoredDocument<TDocument>(TDocument Document, long SeqNo, long PrimaryTerm)
    where TDocument : class;

/// <summary>
/// A document to write. With <see cref="SeqNo"/> and <see cref="PrimaryTerm"/> the write succeeds only if nobody
/// changed the document since it was read; without them it succeeds only if the document does not exist yet.
/// </summary>
public sealed record DocumentWrite<TDocument>(string Id, TDocument Document, long? SeqNo, long? PrimaryTerm)
    where TDocument : class;

/// <summary>Where the projected documents live. Infrastructure implements it over an OpenSearch index or alias.</summary>
public interface IDocumentStore<TDocument>
    where TDocument : class
{
    Task<IReadOnlyDictionary<string, StoredDocument<TDocument>>> GetAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken);

    /// <summary>Writes the documents and returns the ids whose conditional write lost to another writer.</summary>
    Task<IReadOnlyList<string>> WriteAsync(IReadOnlyList<DocumentWrite<TDocument>> writes, CancellationToken cancellationToken);
}

/// <summary>
/// Applies batches of events to the stores. It reads the documents a batch touches in one request, runs the
/// events through the projection in the order they arrived, and writes the changed documents in one request, each
/// conditional on the version it read. A document someone else changed in between is read again and the batch's
/// events for it are replayed on top, so two writers (the live consumer and a rebuild finishing, say) never lose
/// each other's work.
/// </summary>
/// <remarks>
/// Idempotency lives in the projection, not here: an event older than what the document holds changes nothing.
/// That is why this service has no inbox, and why a replay of the whole topic is safe at any moment.
/// </remarks>
public sealed class ProjectionWriter
{
    // A document is contended by at most the consumers of its one partition and a finishing rebuild. Past a handful
    // of lost races something is wrong, and failing lets the consumer retry and then dead-letter the batch.
    private const int MaxConflictRounds = 8;

    private readonly Lane<StayDocument> _stays;
    private readonly Lane<CarLocationDocument> _cars;
    private readonly TimeProvider _clock;
    private readonly bool _measureLag;

    /// <param name="measureLag">
    /// True for the live consumer. A rebuild replays old events on purpose, and their age says nothing about how
    /// far behind the projection is, so it leaves the lag metric alone.
    /// </param>
    public ProjectionWriter(
        IDocumentStore<StayDocument> stays,
        IDocumentStore<CarLocationDocument> cars,
        TimeProvider clock,
        bool measureLag)
    {
        _stays = new Lane<StayDocument>(stays, new StayProjection());
        _cars = new Lane<CarLocationDocument>(cars, new CarProjection());
        _clock = clock;
        _measureLag = measureLag;
    }

    /// <summary>True for the event types the projection reads. The consumer skips the rest without calling the writer.</summary>
    public bool Reads(IIntegrationEvent message) => _stays.Projection.Reads(message) || _cars.Projection.Reads(message);

    /// <summary>
    /// Applies <paramref name="batch"/> and returns one outcome per event, in order. Events the projection does not
    /// read are an error here: filtering is the consumer's job, so a mistake there does not pass unnoticed.
    /// </summary>
    public async Task<IReadOnlyList<ProjectionOutcome>> ApplyAsync(IReadOnlyList<ProjectionEvent> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        ProjectionOutcome[] outcomes = new ProjectionOutcome[batch.Count];
        List<int> stays = [];
        List<int> cars = [];
        for (int i = 0; i < batch.Count; i++)
        {
            IIntegrationEvent message = batch[i].Event;
            if (_stays.Projection.Reads(message))
            {
                stays.Add(i);
            }
            else if (_cars.Projection.Reads(message))
            {
                cars.Add(i);
            }
            else
            {
                throw new ArgumentException($"{message.GetType().Name} is not a listing or availability event.", nameof(batch));
            }
        }

        // One day of grace on both sides of "today": properties sit in every time zone, and a night that is still
        // "tonight" over there must not be trimmed here.
        int firstDay = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).DayNumber - 1;
        await _stays.ApplyAsync(batch, stays, firstDay, outcomes, cancellationToken);
        await _cars.ApplyAsync(batch, cars, firstDay, outcomes, cancellationToken);

        DateTimeOffset indexed = _clock.GetUtcNow();
        for (int i = 0; i < batch.Count; i++)
        {
            if (_measureLag)
            {
                SearchTelemetry.Handled(batch[i].Event.GetType().Name, outcomes[i], indexed - batch[i].OccurredAt);
            }
            else
            {
                SearchTelemetry.Skipped(batch[i].Event.GetType().Name, "replayed");
            }
        }

        return outcomes;
    }

    private sealed class Lane<TDocument>(IDocumentStore<TDocument> store, IProjection<TDocument> projection)
        where TDocument : class
    {
        public IProjection<TDocument> Projection { get; } = projection;

        public async Task ApplyAsync(
            IReadOnlyList<ProjectionEvent> batch,
            List<int> indexes,
            int firstDay,
            ProjectionOutcome[] outcomes,
            CancellationToken cancellationToken)
        {
            if (indexes.Count == 0)
            {
                return;
            }

            // Events of one document stay in arrival order; documents are independent of each other.
            Dictionary<string, List<int>> byDocument = new(StringComparer.Ordinal);
            foreach (int index in indexes)
            {
                string key = Projection.KeyOf(batch[index].Event);
                if (!byDocument.TryGetValue(key, out List<int>? list))
                {
                    byDocument[key] = list = [];
                }

                list.Add(index);
            }

            List<string> pending = [.. byDocument.Keys];
            for (int round = 0; pending.Count > 0; round++)
            {
                if (round == MaxConflictRounds)
                {
                    throw new InvalidOperationException($"{pending.Count} document(s) kept changing under the projection; giving up after {MaxConflictRounds} rounds.");
                }

                IReadOnlyDictionary<string, StoredDocument<TDocument>> stored = await store.GetAsync(pending, cancellationToken);
                List<DocumentWrite<TDocument>> writes = [];
                foreach (string key in pending)
                {
                    stored.TryGetValue(key, out StoredDocument<TDocument>? existing);
                    TDocument? document = existing?.Document;
                    bool changed = false;
                    foreach (int index in byDocument[key])
                    {
                        document = Projection.Apply(document, batch[index].Event, firstDay, out ProjectionOutcome outcome);
                        outcomes[index] = outcome;
                        changed |= outcome == ProjectionOutcome.Applied;
                    }

                    if (changed)
                    {
                        writes.Add(new DocumentWrite<TDocument>(key, document!, existing?.SeqNo, existing?.PrimaryTerm));
                    }
                }

                pending = writes.Count == 0 ? [] : [.. await store.WriteAsync(writes, cancellationToken)];
            }
        }
    }
}
