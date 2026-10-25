using Viatica.Booking.Domain;
using Viatica.Kernel;

namespace Viatica.Booking.UnitTests;

/// <summary>What an owner does with the request for one item. A fate is picked per item, so a run mixes outcomes.</summary>
internal enum Fate
{
    /// <summary>Stay or car: the hold is placed. Flight: the order is created.</summary>
    Succeed,

    /// <summary>The owner refuses.</summary>
    Refuse,

    /// <summary>
    /// The owner refuses, and then a redelivered request succeeds and answers again: a hold or order nobody wants
    /// appears after the trip has given up on the item (docs: "A refused hold is not remembered by the owner").
    /// </summary>
    RefuseThenSucceedLate,

    /// <summary>Flight: the airline refuses to ticket.</summary>
    RefuseTicketing,

    /// <summary>Flight: the airline's void window is closed, so a void becomes a cancel with a refund.</summary>
    VoidWindowClosed,
}

internal sealed record WorldScript(IReadOnlyList<(ItemKind Kind, Fate Fate)> Items, bool DeclinePayment, bool RefuseCapture = false)
{
    public static WorldScript Of(bool declinePayment, params (ItemKind, Fate)[] items) => new(items, declinePayment);
}

/// <summary>
/// The four owners as the service docs describe them, small enough to read: Stays and Cars hold, commit, release,
/// cancel and expire; Flights orders, tickets, voids and cancels; Payments authorises, captures, voids and refunds.
/// The world answers every directive the trip sends, and answers can be delivered late, twice and in any order the
/// test likes. At the end of a run the owners' own books say whether the trip left anything behind.
/// </summary>
internal sealed class SagaWorld
{
    public static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
    public static readonly SagaTimings Timings = SagaTimings.Default;

    private readonly Dictionary<Guid, Fate> fates = [];
    private readonly Dictionary<Guid, (decimal Total, DateTimeOffset Expires, string State)> holds = [];
    private readonly Dictionary<Guid, (Guid OrderId, string State, decimal Total)> orders = [];
    private readonly List<Action<SagaWorld>> lateWork = [];
    private readonly bool declinePayment;
    private readonly bool refuseCapture;

    // One FIFO queue per owner, like one partition per topic: an owner's answers arrive in the order it gave them.
    private readonly Dictionary<string, List<Message>> queues = new()
    {
        ["stays"] = [],
        ["cars"] = [],
        ["flights"] = [],
        ["payments"] = [],
    };

    public SagaWorld(WorldScript script, int seed)
    {
        Random = new Random(seed);
        declinePayment = script.DeclinePayment;
        refuseCapture = script.RefuseCapture;
        Now = Start;
        Trip = Trip.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Now);
        Trip.TakeDirectives();

        foreach ((ItemKind kind, Fate fate) in script.Items)
        {
            Guid id = Guid.CreateVersion7();
            NewItem item = kind switch
            {
                ItemKind.Stay => new NewItem(id, kind, "stay-token", "A stay", Money.Eur(200m), 1, null, Now.AddMinutes(15)),
                ItemKind.Car => new NewItem(id, kind, "car-token", "A car", Money.Eur(80m), 1, 30, Now.AddMinutes(15)),
                _ => new NewItem(id, kind, "offer-1", "A flight", Money.Eur(120m), 1, null, null),
            };
            Trip.AddItem(item, Now);
            fates[id] = fate;
        }

        Trip.TakeDirectives();
    }

    public Random Random { get; }

    public Trip Trip { get; }

    public DateTimeOffset Now { get; private set; }

    public decimal PaymentAuthorised { get; private set; }

    public string PaymentState { get; private set; } = "none";

    /// <summary>Everything the trip sent, in order, for assertions about what it did and did not ask for.</summary>
    public List<SagaDirective> Sent { get; } = [];

    public List<string> Trace { get; } = [];

    public IReadOnlyDictionary<Guid, (decimal Total, DateTimeOffset Expires, string State)> Holds => holds;

    public IReadOnlyDictionary<Guid, (Guid OrderId, string State, decimal Total)> Orders => orders;

    /// <summary>How many holds or orders appeared after the trip had already given up.</summary>
    public int LateAfterFailure { get; private set; }

    public int Pending => queues.Values.Sum(static q => q.Count);

    public static (SagaWorld World, CheckoutResult Result) CheckedOut(WorldScript script, int seed = 1)
    {
        var world = new SagaWorld(script, seed);
        CheckoutResult result = world.Trip.Checkout("fingerprint", "sealed", Guid.CreateVersion7(), world.Now, Timings);
        world.Apply(result.Directives);
        return (world, result);
    }

    public void Advance(TimeSpan by) => Now += by;

    private readonly Dictionary<string, List<SagaDirective>> inbound = new()
    {
        ["stays"] = [],
        ["cars"] = [],
        ["flights"] = [],
        ["payments"] = [],
    };

    /// <summary>Hands the trip's directives to the owners' inboxes. An owner reads its inbox when the test lets it, so a sweeper can win a race against a commit.</summary>
    public void Apply(IEnumerable<SagaDirective> directives)
    {
        foreach (SagaDirective directive in directives)
        {
            Sent.Add(directive);
            string? owner = OwnerOf(directive);
            if (owner is not null)
            {
                inbound[owner].Add(directive);
            }
        }
    }

    /// <summary>One owner reads the oldest request in its inbox and answers it.</summary>
    public bool Process(string owner)
    {
        List<SagaDirective> box = inbound[owner];
        if (box.Count == 0)
        {
            return false;
        }

        SagaDirective next = box[0];
        box.RemoveAt(0);
        Trace.Add($"owner {owner} reads {next}");
        React(next);
        return true;
    }

    public bool ProcessFromRandomOwner()
    {
        string[] busy = [.. inbound.Where(static q => q.Value.Count > 0).Select(static q => q.Key)];
        return busy.Length != 0 && Process(busy[Random.Next(busy.Length)]);
    }

    private static string? OwnerOf(SagaDirective directive) => directive switch
    {
        RequestStayHold => "stays",
        RequestCarHold => "cars",
        CommitHold { Kind: ItemKind.Stay } or ReleaseHold { Kind: ItemKind.Stay } or CancelBooking { Kind: ItemKind.Stay } => "stays",
        CommitHold or ReleaseHold or CancelBooking => "cars",
        RequestFlightOrder or RequestFlightTicketing or VoidFlightOrder or CancelFlightOrder => "flights",
        AuthorizePayment or CapturePayment or VoidPayment or RefundPayment => "payments",
        _ => null,
    };

    /// <summary>Delivers the oldest answer of one owner to the trip, and lets the owners react to what the trip does next.</summary>
    public bool DeliverNext(string owner)
    {
        List<Message> queue = queues[owner];
        if (queue.Count == 0)
        {
            return false;
        }

        Message message = queue[0];
        queue.RemoveAt(0);
        Trace.Add($"deliver from {owner}");
        Apply(message.Deliver(Trip));
        delivered.Add((owner, message));
        return true;
    }

    /// <summary>Delivers a random answer, not necessarily the oldest. Real brokers do not do this; the trip must cope anyway.</summary>
    public bool DeliverAny()
    {
        List<Message> all = [.. queues.Values.SelectMany(static q => q)];
        if (all.Count == 0)
        {
            return false;
        }

        Message pick = all[Random.Next(all.Count)];
        foreach (List<Message> queue in queues.Values)
        {
            if (queue.Remove(pick))
            {
                break;
            }
        }

        Trace.Add("deliver any");
        Apply(pick.Deliver(Trip));
        return true;
    }

    public bool DeliverFromRandomOwner()
    {
        string[] busy = [.. queues.Where(static q => q.Value.Count > 0).Select(static q => q.Key)];
        return busy.Length != 0 && DeliverNext(busy[Random.Next(busy.Length)]);
    }

    /// <summary>The owner sends the same answer again, as a new event with the same content.</summary>
    public bool Duplicate()
    {
        if (delivered.Count == 0)
        {
            return false;
        }

        (string owner, Message message) = delivered[Random.Next(delivered.Count)];
        queues[owner].Add(message);
        return true;
    }

    private readonly List<(string Owner, Message Message)> delivered = [];

    /// <summary>The owners' sweepers: a hold past its expiry is given back and the trip is told.</summary>
    public void Sweep()
    {
        foreach ((Guid holdId, (decimal total, DateTimeOffset expires, string state)) in holds.ToArray())
        {
            if (state == "held" && expires <= Now)
            {
                holds[holdId] = (total, expires, "expired");
                (Guid item, ItemKind kind) = HoldOwner[holdId];
                Enqueue(kind == ItemKind.Stay ? "stays" : "cars", t => t.OnHoldExpired(item, holdId, Now));
            }
        }
    }

    /// <summary>A refused request is judged again and now succeeds: the owner's answer appears after the trip moved on.</summary>
    public bool RunLateWork()
    {
        if (lateWork.Count == 0)
        {
            return false;
        }

        Action<SagaWorld> work = lateWork[Random.Next(lateWork.Count)];
        lateWork.Remove(work);
        if (Trip.Status is TripStatus.Unwinding or TripStatus.Failed)
        {
            LateAfterFailure++;
        }

        Trace.Add("late work");
        work(this);
        return true;
    }

    public void RunChecks() => Apply(Trip.RunScheduledChecks(Now));

    /// <summary>
    /// Plays the run out until nothing more happens. The clock moves only as far as the trip itself asks (its next
    /// scheduled check), never to the owners' own expiry: an owner's sweeper would clean up whatever the trip leaked
    /// and hide the leak from the books.
    /// </summary>
    public void Settle()
    {
        for (int round = 0; round < 400; round++)
        {
            RunToQuiet();

            if (Pending == 0 && inbound.Values.All(static b => b.Count == 0) && lateWork.Count == 0
                && Trip.Status is TripStatus.Confirmed or TripStatus.Failed or TripStatus.Cancelled && !HasOutstanding())
            {
                return;
            }

            Now = Trip.NextCheckAt is { } next && next > Now ? next : Now.AddMinutes(1);
            Sweep();
            RunChecks();
        }
    }

    /// <summary>Everything in flight is processed and delivered, owners and trip taking turns, until nothing is left.</summary>
    public void RunToQuiet()
    {
        bool moved = true;
        while (moved)
        {
            moved = false;
            while (ProcessFromRandomOwner())
            {
                moved = true;
            }

            while (RunLateWork())
            {
                moved = true;
            }

            while (DeliverFromRandomOwner())
            {
                moved = true;
            }
        }
    }

    private bool HasOutstanding() =>
        Trip.Items.Any(static i => i.Awaiting != ItemRequest.None) || Trip.Payment == PaymentStatus.VoidRequested;

    // The owners.

    private Dictionary<Guid, (Guid ItemId, ItemKind Kind)> HoldOwner { get; } = [];

    private void Enqueue(string owner, Func<Trip, IReadOnlyList<SagaDirective>> deliver) =>
        queues[owner].Add(new Message(deliver));

    private void React(SagaDirective directive)
    {
        switch (directive)
        {
            case RequestStayHold h:
                PlaceHold(ItemKind.Stay, h.ItemId, h.HoldId, 200m);
                break;
            case RequestCarHold h:
                PlaceHold(ItemKind.Car, h.ItemId, h.HoldId, 80m);
                break;
            case CommitHold h when holds.TryGetValue(h.HoldId, out var hold) && hold.State is "held" or "committed":
                holds[h.HoldId] = hold with { State = "committed" };
                Enqueue(Owner(h.Kind), t => t.OnHoldCommitted(h.ItemId, h.HoldId, Now));
                break;
            case ReleaseHold h:
                if (holds.TryGetValue(h.HoldId, out var held) && held.State == "committed")
                {
                    break; // the owner ignores a release for a committed hold
                }

                if (held.State == "held")
                {
                    holds[h.HoldId] = held with { State = "released" };
                }

                Enqueue(Owner(h.Kind), t => t.OnHoldReleased(h.ItemId, h.HoldId, Now));
                break;
            case CancelBooking h when holds.TryGetValue(h.HoldId, out var sold) && sold.State is "committed" or "cancelled":
                holds[h.HoldId] = sold with { State = "cancelled" };
                Enqueue(Owner(h.Kind), t => t.OnBookingCancelled(h.ItemId, h.HoldId, Now));
                break;

            case RequestFlightOrder r:
                CreateOrder(r.ItemId, r.ExpectedTotal.Amount);
                break;
            case RequestFlightTicketing r:
                Ticket(r);
                break;
            case VoidFlightOrder r:
                Void(r);
                break;
            case CancelFlightOrder r when orders.TryGetValue(r.ItemId, out var order) && order.State is "ticketed" or "cancelled":
                orders[r.ItemId] = order with { State = "cancelled" };
                Enqueue("flights", t => t.OnFlightOrderCancelled(r.ItemId, r.OrderId, Money.Eur(order.Total / 2), Now));
                break;

            case AuthorizePayment a:
                if (PaymentState is "none")
                {
                    PaymentState = declinePayment ? "declined" : "authorised";
                    PaymentAuthorised = a.Amount.Amount;
                }

                Guid paymentId = a.PaymentId;
                Enqueue(
                    "payments",
                    t => PaymentState == "declined" ? t.OnPaymentDeclined(paymentId, Now) : t.OnPaymentAuthorized(paymentId, Now));
                break;
            case CapturePayment c when refuseCapture && PaymentState is "authorised":
                // Payments will not capture it (a money rule, or the provider) and says so.
                Enqueue("payments", t => t.OnPaymentOperationRefused(c.PaymentId, PaymentOperationKind.Capture, c.PaymentId, Now));
                break;
            case CapturePayment c when PaymentState is "authorised" or "captured":
                PaymentState = "captured";
                Enqueue("payments", t => t.OnPaymentCaptured(c.PaymentId, Now));
                break;
            case VoidPayment v when PaymentState is "authorised" or "voided":
                PaymentState = "voided";
                Enqueue("payments", t => t.OnPaymentVoided(v.PaymentId, Now));
                break;
            case VoidPayment v:
                // A void for a payment that was declined or never made: refused, which is an answer.
                Enqueue("payments", t => t.OnPaymentOperationRefused(v.PaymentId, PaymentOperationKind.Void, v.PaymentId, Now));
                break;
            case RefundPayment r when PaymentState == "captured":
                Enqueue("payments", t => t.OnPaymentRefunded(r.PaymentId, r.RefundId, Now));
                break;
        }
    }

    private static string Owner(ItemKind kind) => kind == ItemKind.Stay ? "stays" : "cars";

    private void PlaceHold(ItemKind kind, Guid itemId, Guid holdId, decimal total)
    {
        string owner = Owner(kind);
        HoldOwner[holdId] = (itemId, kind);

        if (holds.TryGetValue(holdId, out var existing))
        {
            if (existing.State is "held" or "committed")
            {
                AnswerPlaced(owner, itemId, holdId, existing.Total, existing.Expires);
            }

            return;
        }

        switch (fates[itemId])
        {
            case Fate.Refuse:
                Enqueue(owner, t => t.OnHoldRejected(itemId, holdId, "SoldOut", Now));
                break;

            case Fate.RefuseThenSucceedLate:
                Enqueue(owner, t => t.OnHoldRejected(itemId, holdId, "SoldOut", Now));
                lateWork.Add(w => w.Judge(owner, itemId, holdId, total));
                break;

            default:
                Judge(owner, itemId, holdId, total);
                break;
        }
    }

    private void Judge(string owner, Guid itemId, Guid holdId, decimal total)
    {
        DateTimeOffset expires = Now.AddMinutes(15);
        holds[holdId] = (total, expires, "held");
        AnswerPlaced(owner, itemId, holdId, total, expires);
    }

    private void AnswerPlaced(string owner, Guid itemId, Guid holdId, decimal total, DateTimeOffset expires)
    {
        Enqueue(owner, t => t.OnHoldPlaced(
            itemId, holdId, Money.Eur(total), expires,
            new ItemTerms(Guid.Empty, Now.AddDays(30), 3, 48, Penalty.FirstUnit), Now));
    }

    private void CreateOrder(Guid itemId, decimal total)
    {
        if (orders.TryGetValue(itemId, out var existing))
        {
            if (existing.State is "booked" or "ticketed")
            {
                AnswerCreated(itemId, existing.OrderId, existing.Total);
            }

            return;
        }

        switch (fates[itemId])
        {
            case Fate.Refuse:
                Enqueue("flights", t => t.OnFlightOrderRejected(itemId, "SupplierUnavailable", Now));
                break;

            case Fate.RefuseThenSucceedLate:
                Enqueue("flights", t => t.OnFlightOrderRejected(itemId, "SupplierUnavailable", Now));
                lateWork.Add(w => w.MakeOrder(itemId, total));
                break;

            default:
                MakeOrder(itemId, total);
                break;
        }
    }

    private void MakeOrder(Guid itemId, decimal total)
    {
        Guid orderId = Guid.CreateVersion7();
        orders[itemId] = (orderId, "booked", total);
        AnswerCreated(itemId, orderId, total);
    }

    private void AnswerCreated(Guid itemId, Guid orderId, decimal total)
    {
        DateTimeOffset limit = Now.AddMinutes(60);
        Enqueue("flights", t => t.OnFlightOrderCreated(itemId, orderId, Money.Eur(total), limit, Now));
    }

    private void Ticket(RequestFlightTicketing request)
    {
        Guid itemId = request.ItemId;
        Guid orderId = request.OrderId;

        if (!orders.TryGetValue(itemId, out var order) || order.OrderId != orderId || order.State is "voided" or "cancelled")
        {
            Enqueue("flights", t => t.OnFlightTicketingRefused(itemId, orderId, "UnknownOrder", Now));
        }
        else if (order.State == "ticketed")
        {
            Enqueue("flights", t => t.OnFlightTicketed(itemId, orderId, Now));
        }
        else if (fates[itemId] == Fate.RefuseTicketing)
        {
            Enqueue("flights", t => t.OnFlightTicketingRefused(itemId, orderId, "Refused", Now));
        }
        else
        {
            orders[itemId] = order with { State = "ticketed" };
            Enqueue("flights", t => t.OnFlightTicketed(itemId, orderId, Now));
        }
    }

    private void Void(VoidFlightOrder request)
    {
        Guid itemId = request.ItemId;
        Guid orderId = request.OrderId;

        if (!orders.TryGetValue(itemId, out var order))
        {
            Enqueue("flights", t => t.OnFlightOrderVoided(itemId, orderId, Now));
            return;
        }

        switch (order.State)
        {
            case "voided":
                Enqueue("flights", t => t.OnFlightOrderVoided(itemId, orderId, Now));
                break;
            case "cancelled":
                Enqueue("flights", t => t.OnFlightOrderCancelled(itemId, orderId, Money.Eur(order.Total / 2), Now));
                break;
            case "ticketed" when fates[itemId] == Fate.VoidWindowClosed:
                orders[itemId] = order with { State = "cancelled" };
                Enqueue("flights", t => t.OnFlightOrderCancelled(itemId, orderId, Money.Eur(order.Total / 2), Now));
                break;
            default:
                orders[itemId] = order with { State = "voided" };
                Enqueue("flights", t => t.OnFlightOrderVoided(itemId, orderId, Now));
                break;
        }
    }

    private sealed record Message(Func<Trip, IReadOnlyList<SagaDirective>> Deliver);

    // The books.

    /// <summary>What the owners' own records say about a run. Empty when the trip left nothing behind and charged correctly.</summary>
    public IReadOnlyList<string> Violations()
    {
        List<string> problems = [];
        TripStatus status = Trip.Status;

        if (status is TripStatus.Confirmed or TripStatus.Cancelled)
        {
            foreach (TripItem item in Trip.Items)
            {
                if (item.Status is not (ItemStatus.Committed or ItemStatus.Ticketed or ItemStatus.Cancelled))
                {
                    problems.Add($"confirmed trip, item {item.Kind} is {item.Status}");
                }
            }

            foreach ((Guid holdId, var hold) in holds)
            {
                TripItem item = Trip.Items.Single(i => i.HoldId == holdId);
                string expected = item.Status == ItemStatus.Cancelled ? "cancelled" : "committed";
                if (hold.State != expected)
                {
                    problems.Add($"confirmed trip, owner has hold {hold.State}, expected {expected}");
                }
            }

            if (PaymentState != "captured")
            {
                problems.Add($"confirmed trip, payment is {PaymentState}");
            }

            decimal sum = Trip.Items.Sum(static i => i.TotalAmount ?? 0m);
            if (PaymentAuthorised != sum)
            {
                problems.Add($"confirmed trip, authorised {PaymentAuthorised} but items total {sum}");
            }
        }
        else if (status == TripStatus.Failed)
        {
            foreach ((Guid holdId, var hold) in holds)
            {
                if (hold.State is "held" or "committed")
                {
                    problems.Add($"failed trip, owner still has hold {holdId} {hold.State}");
                }
            }

            foreach ((Guid itemId, var order) in orders)
            {
                if (order.State is "booked" or "ticketed")
                {
                    problems.Add($"failed trip, airline still has order {itemId} {order.State}");
                }
            }

            if (PaymentState is "authorised" or "captured")
            {
                problems.Add($"failed trip, payment is {PaymentState}");
            }

            if (Trip.SealedTravellerData is not null)
            {
                problems.Add("failed trip still holds traveller data");
            }
        }
        else
        {
            problems.Add($"the trip ended {status}");
        }

        return problems;
    }
}
