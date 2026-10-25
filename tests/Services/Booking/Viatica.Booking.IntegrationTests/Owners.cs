using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Kernel.Pii;
using Viatica.Testing;

namespace Viatica.Booking.IntegrationTests;

/// <summary>What an owner does with the request for one item.</summary>
internal enum Fate
{
    /// <summary>Stay or car: the hold is placed. Flight: the order is created.</summary>
    Succeed,

    /// <summary>The owner refuses.</summary>
    Refuse,

    /// <summary>The owner refuses, and a request judged again later (<see cref="Owners.RunLateWorkAsync"/>) succeeds.</summary>
    RefuseThenSucceedLate,

    /// <summary>Flight: the airline refuses to ticket.</summary>
    RefuseTicketing,

    /// <summary>Flight: the airline's void window is closed, so a void becomes a cancel with a refund.</summary>
    VoidWindowClosed,
}

/// <summary>How the stand-in treats one item: what it costs, whose it is, when it starts, what its policy says.</summary>
internal sealed record ItemPlan
{
    public Fate Fate { get; init; } = Fate.Succeed;

    public decimal Total { get; init; } = 100m;

    public Guid PartnerId { get; init; } = Guid.Parse("c0000000-0000-4000-8000-000000000001");

    public TimeSpan StartsIn { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Nights of a stay, or days of a rental.</summary>
    public int Units { get; init; } = 3;

    public int FreeHours { get; init; } = 48;

    public CancellationPenalty Penalty { get; init; } = CancellationPenalty.FirstUnit;

    public TimeSpan TicketingLimit { get; init; } = TimeSpan.FromMinutes(60);
}

internal sealed record BookingTopics(string Booking, string Stays, string Cars, string Flights, string Payments);

internal abstract class StandInDb(DbContextOptions options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddMessaging();
}

internal sealed class StaysDb(DbContextOptions<StaysDb> options) : StandInDb(options);

internal sealed class CarsDb(DbContextOptions<CarsDb> options) : StandInDb(options);

internal sealed class FlightsDb(DbContextOptions<FlightsDb> options) : StandInDb(options);

internal sealed class PaymentsDb(DbContextOptions<PaymentsDb> options) : StandInDb(options);

/// <summary>
/// Stays, Cars, Flights and Payments as far as Booking can tell: one consumer of <c>booking.v1</c> that answers each
/// request on the owner's own topic through an outbox, with the behaviour the service docs describe (a commit past the
/// expiry still wins until the sweeper acts, a release for a hold never placed is answered, a void for an unknown
/// order is answered, a cancel for an unknown order is not). Tests choose each item's fate, pause a kind of request to
/// make answers late, and say things an owner would not say once: a hold after a refusal, an answer twice.
/// </summary>
internal sealed class Owners : IAsyncDisposable
{
    private readonly IHost host;
    private readonly IConsumer<string, byte[]> consumer;
    private readonly CancellationTokenSource stop = new();
    private readonly FakeTimeProvider clock;
    private readonly PiiSealer passengerKey;
    private readonly SemaphoreSlim reacting = new(1, 1);
    private readonly object gate = new();
    private Task loop = Task.CompletedTask;

    private readonly Dictionary<Guid, ItemPlan> plans = [];
    private readonly List<IIntegrationEvent> received = [];
    private readonly List<IIntegrationEvent> said = [];
    private readonly List<IIntegrationEvent> deferred = [];
    private readonly HashSet<Type> paused = [];
    private readonly List<Func<Task>> late = [];
    private readonly Dictionary<Guid, Hold> holds = [];
    private readonly Dictionary<Guid, Order> orders = [];
    private readonly Dictionary<Guid, Payment> payments = [];
    private readonly Dictionary<Guid, IReadOnlyList<PassengerDetails>> passengers = [];
    private readonly Dictionary<Guid, string> paymentTokens = [];
    private readonly HashSet<Guid> declinedTrips = [];

    private Owners(IHost host, IConsumer<string, byte[]> consumer, FakeTimeProvider clock, byte[] passengerKey)
    {
        this.host = host;
        this.consumer = consumer;
        this.clock = clock;
        this.passengerKey = new PiiSealer(passengerKey);
    }

    public sealed record Hold(Guid HoldId, Guid TripId, Guid ItemId, bool Stay, decimal Total, string State);

    public sealed record Order(Guid OrderId, Guid TripId, Guid ItemId, decimal Total, string State);

    public sealed record Payment(Guid PaymentId, Guid TripId, decimal Amount, string State);

    public static async Task<Owners> StartAsync(
        PostgresFixture postgres, KafkaFixture kafka, BookingTopics topics, FakeTimeProvider clock, byte[] passengerKey)
    {
        string stays = new Npgsql.NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync("standin_stays")) { MaxPoolSize = 5 }.ConnectionString;
        string cars = new Npgsql.NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync("standin_cars")) { MaxPoolSize = 5 }.ConnectionString;
        string flights = new Npgsql.NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync("standin_flights")) { MaxPoolSize = 5 }.ConnectionString;
        string payments = new Npgsql.NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync("standin_payments")) { MaxPoolSize = 5 }.ConnectionString;

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<StaysDb>(o => o.UseNpgsql(stays));
        builder.Services.AddDbContext<CarsDb>(o => o.UseNpgsql(cars));
        builder.Services.AddDbContext<FlightsDb>(o => o.UseNpgsql(flights));
        builder.Services.AddDbContext<PaymentsDb>(o => o.UseNpgsql(payments));
        builder.Services.AddMessaging(
            new IntegrationEventTypes(ContractsJsonContext.Default, EventCatalog.TopicByType.Keys),
            options => options.BootstrapServers = kafka.BootstrapServers);
        builder.Services.AddOutboxRelay<StaysDb>(topics.Stays, o => o.PollInterval = TimeSpan.FromMilliseconds(30));
        builder.Services.AddOutboxRelay<CarsDb>(topics.Cars, o => o.PollInterval = TimeSpan.FromMilliseconds(30));
        builder.Services.AddOutboxRelay<FlightsDb>(topics.Flights, o => o.PollInterval = TimeSpan.FromMilliseconds(30));
        builder.Services.AddOutboxRelay<PaymentsDb>(topics.Payments, o => o.PollInterval = TimeSpan.FromMilliseconds(30));

        IHost host = builder.Build();
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<StaysDb>().Database.EnsureCreatedAsync();
            await scope.ServiceProvider.GetRequiredService<CarsDb>().Database.EnsureCreatedAsync();
            await scope.ServiceProvider.GetRequiredService<FlightsDb>().Database.EnsureCreatedAsync();
            await scope.ServiceProvider.GetRequiredService<PaymentsDb>().Database.EnsureCreatedAsync();
        }

        await host.StartAsync();

        IConsumer<string, byte[]> consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = $"owners-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(topics.Booking);

        var owners = new Owners(host, consumer, clock, passengerKey);
        owners.loop = Task.Run(owners.LoopAsync);
        return owners;
    }

    // What the tests set up.

    public ItemPlan PlanOf(Guid itemId)
    {
        lock (gate)
        {
            return plans.GetValueOrDefault(itemId) ?? new ItemPlan();
        }
    }

    public void Plan(Guid itemId, ItemPlan plan)
    {
        lock (gate)
        {
            plans[itemId] = plan;
        }
    }

    /// <summary>The card of this trip will be declined.</summary>
    public void Decline(Guid tripId)
    {
        lock (gate)
        {
            declinedTrips.Add(tripId);
        }
    }

    /// <summary>Requests of this type are read and held back until <see cref="ResumeAsync"/>, so the answer comes late.</summary>
    public void Pause<T>()
        where T : IIntegrationEvent
    {
        lock (gate)
        {
            paused.Add(typeof(T));
        }
    }

    public async Task ResumeAsync<T>()
        where T : IIntegrationEvent
    {
        List<IIntegrationEvent> held;
        lock (gate)
        {
            paused.Remove(typeof(T));
            held = [.. deferred.Where(static e => e is T)];
            deferred.RemoveAll(static e => e is T);
        }

        foreach (IIntegrationEvent request in held)
        {
            await ReactAsync(request);
        }
    }

    /// <summary>Stops holding anything back and answers what was held, so a test starts with every owner responsive.</summary>
    public async Task ResumeAllAsync()
    {
        List<IIntegrationEvent> held;
        lock (gate)
        {
            paused.Clear();
            held = [.. deferred];
            deferred.Clear();
        }

        foreach (IIntegrationEvent request in held)
        {
            await ReactAsync(request);
        }
    }

    // What the tests ask.

    /// <summary>Every answer the owners have given, in the order they gave it.</summary>
    public IReadOnlyList<IIntegrationEvent> Said
    {
        get
        {
            lock (gate)
            {
                return [.. said];
            }
        }
    }

    public IReadOnlyList<IIntegrationEvent> Received
    {
        get
        {
            lock (gate)
            {
                return [.. received];
            }
        }
    }

    /// <summary>What Booking has sent about one trip so far, in the order the owners read it. Every event on booking.v1 carries a trip id.</summary>
    public IReadOnlyList<IIntegrationEvent> ReceivedFor(Guid tripId) =>
        [.. Received.Where(e => e.GetType().GetProperty("TripId")?.GetValue(e) is Guid id && id == tripId)];

    public int Count<T>(Func<T, bool>? where = null)
        where T : IIntegrationEvent =>
        Received.OfType<T>().Count(e => where is null || where(e));

    public async Task<T> WaitForAsync<T>(Func<T, bool>? where = null, int atLeast = 1)
        where T : IIntegrationEvent
    {
        await Eventually.MatchesAsync(() => Task.FromResult(Count(where)), count => count >= atLeast);
        return Received.OfType<T>().Where(e => where is null || where(e)).ElementAt(atLeast - 1);
    }

    public Hold? HoldOfItem(Guid itemId)
    {
        lock (gate)
        {
            return holds.Values.FirstOrDefault(h => h.ItemId == itemId);
        }
    }

    public Order? OrderOfItem(Guid itemId)
    {
        lock (gate)
        {
            return orders.GetValueOrDefault(itemId);
        }
    }

    public Payment? PaymentOfTrip(Guid tripId)
    {
        lock (gate)
        {
            return payments.GetValueOrDefault(tripId);
        }
    }

    public IReadOnlyList<PassengerDetails>? PassengersOf(Guid itemId)
    {
        lock (gate)
        {
            return passengers.GetValueOrDefault(itemId);
        }
    }

    public string? PaymentTokenOf(Guid tripId)
    {
        lock (gate)
        {
            return paymentTokens.GetValueOrDefault(tripId);
        }
    }

    public IReadOnlyList<Hold> AllHolds
    {
        get
        {
            lock (gate)
            {
                return [.. holds.Values];
            }
        }
    }

    public IReadOnlyList<Order> AllOrders
    {
        get
        {
            lock (gate)
            {
                return [.. orders.Values];
            }
        }
    }

    // What an owner does on its own.

    /// <summary>The owner's sweeper takes a held hold back and says so.</summary>
    public async Task ExpireAsync(Guid itemId)
    {
        Hold? hold;
        lock (gate)
        {
            hold = holds.Values.FirstOrDefault(h => h.ItemId == itemId && h.State == "held");
            if (hold is not null)
            {
                holds[hold.HoldId] = hold with { State = "expired" };
            }
        }

        if (hold is not null)
        {
            await SendAsync(hold.Stay
                ? new StayHoldExpired(hold.HoldId, hold.TripId, hold.ItemId)
                : new CarHoldExpired(hold.HoldId, hold.TripId, hold.ItemId));
        }
    }

    /// <summary>A request that was refused is judged again and now succeeds.</summary>
    public async Task RunLateWorkAsync()
    {
        List<Func<Task>> work;
        lock (gate)
        {
            work = [.. late];
            late.Clear();
        }

        foreach (Func<Task> item in work)
        {
            await item();
        }
    }

    /// <summary>An owner says something the test wants Booking to hear, as a new event with its own id.</summary>
    public Task SayAsync(IIntegrationEvent message) => SendAsync(message);

    private async Task SendAsync(IIntegrationEvent message)
    {
        lock (gate)
        {
            said.Add(message);
        }

        string topic = EventCatalog.TopicByType[message.GetType()];
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        StandInDb db = topic switch
        {
            Topics.Stays => scope.ServiceProvider.GetRequiredService<StaysDb>(),
            Topics.Cars => scope.ServiceProvider.GetRequiredService<CarsDb>(),
            Topics.Flights => scope.ServiceProvider.GetRequiredService<FlightsDb>(),
            Topics.Payments => scope.ServiceProvider.GetRequiredService<PaymentsDb>(),
            _ => throw new InvalidOperationException($"{message.GetType().Name} is not an owner's event."),
        };

        db.AddIntegrationEvent(message);
        await db.SaveChangesAsync();
    }

    // The conversation.

    private async Task LoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            ConsumeResult<string, byte[]>? result;
            try
            {
                result = consumer.Consume(TimeSpan.FromMilliseconds(100));
            }
            catch (ConsumeException ex) when (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
            {
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (result is not { IsPartitionEOF: false, Message: not null })
            {
                continue;
            }

            if (Read(result.Message.Value) is { } request)
            {
                bool hold;
                lock (gate)
                {
                    received.Add(request);
                    hold = paused.Contains(request.GetType());
                    if (hold)
                    {
                        deferred.Add(request);
                    }
                }

                if (!hold)
                {
                    await ReactAsync(request);
                }
            }
        }
    }

    private static IIntegrationEvent? Read(byte[] value)
    {
        using JsonDocument document = JsonDocument.Parse(value);
        string type = document.RootElement.GetProperty("type").GetString()!;
        return EventCatalog.TypeByName.TryGetValue(type, out Type? clr)
            ? (IIntegrationEvent?)JsonSerializer.Deserialize(document.RootElement.GetProperty("data").GetRawText(), clr, ContractsJsonContext.Default)
            : null;
    }

    private async Task ReactAsync(IIntegrationEvent request)
    {
        await reacting.WaitAsync();
        try
        {
            switch (request)
            {
                case StayHoldRequested r:
                    await PlaceHoldAsync(true, r.TripId, r.ItemId, r.HoldId);
                    break;
                case CarHoldRequested r:
                    await PlaceHoldAsync(false, r.TripId, r.ItemId, r.HoldId);
                    break;
                case StayHoldCommitRequested r:
                    await CommitAsync(true, r.TripId, r.ItemId, r.HoldId);
                    break;
                case CarHoldCommitRequested r:
                    await CommitAsync(false, r.TripId, r.ItemId, r.HoldId);
                    break;
                case StayHoldReleaseRequested r:
                    await ReleaseAsync(true, r.TripId, r.ItemId, r.HoldId);
                    break;
                case CarHoldReleaseRequested r:
                    await ReleaseAsync(false, r.TripId, r.ItemId, r.HoldId);
                    break;
                case StayBookingCancelRequested r:
                    await CancelBookingAsync(true, r.TripId, r.ItemId, r.HoldId);
                    break;
                case CarBookingCancelRequested r:
                    await CancelBookingAsync(false, r.TripId, r.ItemId, r.HoldId);
                    break;
                case FlightOrderRequested r:
                    await OrderAsync(r);
                    break;
                case FlightTicketingRequested r:
                    await TicketAsync(r);
                    break;
                case FlightOrderVoidRequested r:
                    await VoidOrderAsync(r);
                    break;
                case FlightOrderCancelRequested r:
                    await CancelOrderAsync(r);
                    break;
                case PaymentAuthorizationRequested r:
                    await AuthorizeAsync(r);
                    break;
                case PaymentCaptureRequested r:
                    await CaptureAsync(r);
                    break;
                case PaymentVoidRequested r:
                    await VoidPaymentAsync(r);
                    break;
                case PaymentRefundRequested r:
                    await RefundAsync(r);
                    break;
            }
        }
        finally
        {
            reacting.Release();
        }
    }

    // Stays and cars.

    private async Task PlaceHoldAsync(bool stay, Guid tripId, Guid itemId, Guid holdId)
    {
        Hold? existing;
        lock (gate)
        {
            existing = holds.GetValueOrDefault(holdId);
        }

        ItemPlan plan = PlanOf(itemId);
        if (existing is not null)
        {
            if (existing.State is "held" or "committed")
            {
                await SendAsync(PlacedEvent(existing, plan));
            }

            return;
        }

        switch (plan.Fate)
        {
            case Fate.Refuse:
                await SendAsync(Rejected(stay, holdId, tripId, itemId));
                break;

            case Fate.RefuseThenSucceedLate:
                await SendAsync(Rejected(stay, holdId, tripId, itemId));
                lock (gate)
                {
                    late.Add(() => JudgeAsync(stay, tripId, itemId, holdId, plan));
                }

                break;

            default:
                await JudgeAsync(stay, tripId, itemId, holdId, plan);
                break;
        }
    }

    private async Task JudgeAsync(bool stay, Guid tripId, Guid itemId, Guid holdId, ItemPlan plan)
    {
        var hold = new Hold(holdId, tripId, itemId, stay, plan.Total, "held");
        lock (gate)
        {
            holds[holdId] = hold;
        }

        await SendAsync(PlacedEvent(hold, plan));
    }

    private IIntegrationEvent PlacedEvent(Hold hold, ItemPlan plan)
    {
        DateTimeOffset now = clock.GetUtcNow();
        var terms = new HoldTerms(plan.PartnerId, now + plan.StartsIn, new CancellationPolicy(plan.FreeHours, plan.Penalty));
        var price = new Price(hold.Total, "EUR");

        if (hold.Stay)
        {
            DateOnly checkIn = DateOnly.FromDateTime((now + plan.StartsIn).UtcDateTime);
            return new StayHoldPlaced(
                hold.HoldId, hold.TripId, hold.ItemId, Guid.NewGuid(), Guid.NewGuid(), checkIn, checkIn.AddDays(plan.Units), 1, price, now.AddMinutes(15), terms);
        }

        return new CarHoldPlaced(
            hold.HoldId, hold.TripId, hold.ItemId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            now + plan.StartsIn, now + plan.StartsIn + TimeSpan.FromDays(plan.Units), price, now.AddMinutes(15), terms);
    }

    private static IIntegrationEvent Rejected(bool stay, Guid holdId, Guid tripId, Guid itemId) => stay
        ? new StayHoldRejected(holdId, tripId, itemId, HoldRejectionReason.SoldOut)
        : new CarHoldRejected(holdId, tripId, itemId, HoldRejectionReason.SoldOut);

    private async Task CommitAsync(bool stay, Guid tripId, Guid itemId, Guid holdId)
    {
        bool done;
        lock (gate)
        {
            Hold? hold = holds.GetValueOrDefault(holdId);
            done = hold is { State: "held" or "committed" };
            if (done)
            {
                holds[holdId] = hold! with { State = "committed" };
            }
        }

        if (done)
        {
            await SendAsync(stay ? new StayHoldCommitted(holdId, tripId, itemId) : new CarHoldCommitted(holdId, tripId, itemId));
        }
    }

    private async Task ReleaseAsync(bool stay, Guid tripId, Guid itemId, Guid holdId)
    {
        bool answer;
        lock (gate)
        {
            Hold? hold = holds.GetValueOrDefault(holdId);
            answer = hold is not { State: "committed" };
            if (hold is { State: "held" })
            {
                holds[holdId] = hold with { State = "released" };
            }
        }

        if (answer)
        {
            await SendAsync(stay ? new StayHoldReleased(holdId, tripId, itemId) : new CarHoldReleased(holdId, tripId, itemId));
        }
    }

    private async Task CancelBookingAsync(bool stay, Guid tripId, Guid itemId, Guid holdId)
    {
        bool answer;
        lock (gate)
        {
            Hold? hold = holds.GetValueOrDefault(holdId);
            answer = hold is { State: "committed" or "cancelled" };
            if (answer)
            {
                holds[holdId] = hold! with { State = "cancelled" };
            }
        }

        if (answer)
        {
            await SendAsync(stay ? new StayBookingCancelled(holdId, tripId, itemId) : new CarBookingCancelled(holdId, tripId, itemId));
        }
    }

    // Flights.

    private async Task OrderAsync(FlightOrderRequested request)
    {
        Order? existing;
        lock (gate)
        {
            existing = orders.GetValueOrDefault(request.ItemId);
        }

        if (existing is not null)
        {
            if (existing.State is "booked" or "ticketed")
            {
                await SendAsync(CreatedEvent(existing, PlanOf(request.ItemId)));
            }

            return;
        }

        if (!passengerKey.TryOpenPassengers(request.EncryptedPassengers, request.TripId, request.ItemId, out IReadOnlyList<PassengerDetails> opened))
        {
            await SendAsync(new FlightOrderRejected(request.TripId, request.ItemId, FlightRejectionReason.InvalidPassengers));
            return;
        }

        lock (gate)
        {
            passengers[request.ItemId] = opened;
        }

        ItemPlan plan = PlanOf(request.ItemId);
        switch (plan.Fate)
        {
            case Fate.Refuse:
                await SendAsync(new FlightOrderRejected(request.TripId, request.ItemId, FlightRejectionReason.SoldOut));
                break;

            case Fate.RefuseThenSucceedLate:
                await SendAsync(new FlightOrderRejected(request.TripId, request.ItemId, FlightRejectionReason.SupplierUnavailable));
                lock (gate)
                {
                    late.Add(() => MakeOrderAsync(request.TripId, request.ItemId, plan));
                }

                break;

            default:
                await MakeOrderAsync(request.TripId, request.ItemId, plan);
                break;
        }
    }

    private async Task MakeOrderAsync(Guid tripId, Guid itemId, ItemPlan plan)
    {
        var order = new Order(Guid.CreateVersion7(), tripId, itemId, plan.Total, "booked");
        lock (gate)
        {
            orders[itemId] = order;
        }

        await SendAsync(CreatedEvent(order, plan));
    }

    private FlightOrderCreated CreatedEvent(Order order, ItemPlan plan) =>
        new(order.OrderId, order.TripId, order.ItemId, "ZC", "BR" + order.OrderId.ToString("N")[..6].ToUpperInvariant(), clock.GetUtcNow() + plan.TicketingLimit, new Price(order.Total, "EUR"));

    private async Task TicketAsync(FlightTicketingRequested request)
    {
        IIntegrationEvent answer;
        lock (gate)
        {
            Order? order = orders.GetValueOrDefault(request.ItemId);
            if (order is null || order.OrderId != request.OrderId || order.State is "voided" or "cancelled")
            {
                answer = new FlightTicketingRefused(request.OrderId, request.TripId, request.ItemId, "UnknownOrder");
            }
            else if (order.State == "ticketed")
            {
                answer = new FlightTicketed(order.OrderId, request.TripId, request.ItemId, ["0161234567890"]);
            }
            else if (PlanOf(request.ItemId).Fate == Fate.RefuseTicketing)
            {
                answer = new FlightTicketingRefused(order.OrderId, request.TripId, request.ItemId, "Refused");
            }
            else
            {
                orders[request.ItemId] = order with { State = "ticketed" };
                answer = new FlightTicketed(order.OrderId, request.TripId, request.ItemId, ["0161234567890"]);
            }
        }

        await SendAsync(answer);
    }

    private async Task VoidOrderAsync(FlightOrderVoidRequested request)
    {
        IIntegrationEvent answer;
        lock (gate)
        {
            Order? order = orders.GetValueOrDefault(request.ItemId);
            if (order is null || order.State == "voided")
            {
                answer = new FlightOrderVoided(request.OrderId, request.TripId, request.ItemId);
            }
            else if (order.State == "cancelled" || order is { State: "ticketed" } && PlanOf(request.ItemId).Fate == Fate.VoidWindowClosed)
            {
                orders[request.ItemId] = order with { State = "cancelled" };
                answer = new FlightOrderCancelled(order.OrderId, request.TripId, request.ItemId, new Price(order.Total / 2, "EUR"));
            }
            else
            {
                orders[request.ItemId] = order with { State = "voided" };
                answer = new FlightOrderVoided(order.OrderId, request.TripId, request.ItemId);
            }
        }

        await SendAsync(answer);
    }

    private async Task CancelOrderAsync(FlightOrderCancelRequested request)
    {
        IIntegrationEvent? answer = null;
        lock (gate)
        {
            Order? order = orders.GetValueOrDefault(request.ItemId);
            if (order is { State: "ticketed" or "cancelled" })
            {
                // The fare rules: half back, whatever the fare. The tests that care set the total so that this is easy to read.
                orders[request.ItemId] = order with { State = "cancelled" };
                answer = new FlightOrderCancelled(order.OrderId, request.TripId, request.ItemId, new Price(order.Total / 2, "EUR"));
            }
            else if (order is { State: "booked" })
            {
                orders[request.ItemId] = order with { State = "voided" };
                answer = new FlightOrderVoided(order.OrderId, request.TripId, request.ItemId);
            }

            // An order Flights never made gets no answer at all: it refuses to invent a refund.
        }

        if (answer is not null)
        {
            await SendAsync(answer);
        }
    }

    // Payments.

    private async Task AuthorizeAsync(PaymentAuthorizationRequested request)
    {
        bool decline;
        lock (gate)
        {
            paymentTokens[request.TripId] = request.PaymentMethodToken;
            decline = declinedTrips.Contains(request.TripId);
            payments.TryAdd(request.TripId, new Payment(request.PaymentId, request.TripId, request.Amount.Amount, decline ? "declined" : "authorised"));
        }

        await SendAsync(decline
            ? new PaymentDeclined(request.PaymentId, request.TripId, "card_declined")
            : new PaymentAuthorized(request.PaymentId, request.TripId, request.Amount));
    }

    private async Task CaptureAsync(PaymentCaptureRequested request)
    {
        bool done;
        lock (gate)
        {
            Payment? payment = payments.GetValueOrDefault(request.TripId);
            done = payment is { State: "authorised" or "captured" };
            if (done)
            {
                payments[request.TripId] = payment! with { State = "captured", Amount = request.Amount.Amount };
            }
        }

        if (done)
        {
            await SendAsync(new PaymentCaptured(request.PaymentId, request.TripId, request.Amount));
        }
    }

    private async Task VoidPaymentAsync(PaymentVoidRequested request)
    {
        bool done;
        lock (gate)
        {
            Payment? payment = payments.GetValueOrDefault(request.TripId);
            done = payment is { State: "authorised" or "voided" };
            if (done)
            {
                payments[request.TripId] = payment! with { State = "voided" };
            }
        }

        if (done)
        {
            await SendAsync(new PaymentVoided(request.PaymentId, request.TripId));
        }
    }

    private async Task RefundAsync(PaymentRefundRequested request)
    {
        bool done;
        lock (gate)
        {
            done = payments.GetValueOrDefault(request.TripId) is { State: "captured" };
        }

        if (done)
        {
            await SendAsync(new PaymentRefunded(request.PaymentId, request.TripId, request.RefundId, request.Amount));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await loop;
        consumer.Close();
        consumer.Dispose();
        await host.StopAsync();
        host.Dispose();
        reacting.Dispose();
        stop.Dispose();
    }
}
