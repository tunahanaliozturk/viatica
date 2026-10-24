using System.Globalization;
using Viatica.PspSim.Api;

namespace Viatica.PspSim.Ledger;

internal sealed record Token(string Id, TestCard Card, string Last4, int ExpMonth, int ExpYear, DateTimeOffset Created);

internal enum IntentStatus
{
    RequiresCapture,
    Succeeded,
    Canceled,
}

internal sealed class Intent(string id, long amount, string currency, Token token, IReadOnlyDictionary<string, string> metadata, DateTimeOffset created)
{
    public string Id { get; } = id;

    public long Amount { get; } = amount;

    public string Currency { get; } = currency;

    public Token Token { get; } = token;

    public IReadOnlyDictionary<string, string> Metadata { get; } = metadata;

    public DateTimeOffset Created { get; } = created;

    public IntentStatus Status { get; set; } = IntentStatus.RequiresCapture;

    public long AmountReceived { get; set; }

    public long AmountRefunded { get; set; }
}

internal sealed record Refund(string Id, Intent Intent, long Amount, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset Created);

/// <summary>What the provider answered for an idempotency key, and what the key was first used for.</summary>
internal sealed record Recorded(string Key, string Type, string Fingerprint, Reply Reply, DateTimeOffset Created);

/// <summary>The answer to a mutating call, and whether it is a repeat of an earlier one.</summary>
internal readonly record struct Execution(Reply Reply, bool Replayed);

/// <summary>
/// Everything the provider remembers: tokens, payment intents, refunds, and the answer to every idempotency key.
/// It is in memory and gone with the process, which is what a test double should be.
/// </summary>
/// <remarks>
/// One lock guards all of it. A lookup and a replay read the same record a concurrent call writes, and a capture
/// must see the intent a cancel is changing; a simulator at test volumes gains nothing from finer locking, and a
/// single lock cannot deadlock. Add a lock per intent if a load test ever shows contention here.
/// </remarks>
internal sealed class ProviderLedger(TimeProvider clock)
{
    private const long MaxAmount = 99_999_999;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Token> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Intent> _intents = new(StringComparer.Ordinal);
    private readonly List<Refund> _refunds = [];
    private readonly Dictionary<string, Recorded> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);

    /// <summary>Counts a call under an idempotency key. The first is 1.</summary>
    public int NextAttempt(string key)
    {
        lock (_gate)
        {
            return _attempts[key] = _attempts.GetValueOrDefault(key) + 1;
        }
    }

    public CardBehaviour? BehaviourOfToken(string? tokenId)
    {
        lock (_gate)
        {
            return tokenId is not null && _tokens.TryGetValue(tokenId, out Token? token) ? token.Card.Behaviour : null;
        }
    }

    public CardBehaviour? BehaviourOfIntent(string? intentId)
    {
        lock (_gate)
        {
            return intentId is not null && _intents.TryGetValue(intentId, out Intent? intent) ? intent.Token.Card.Behaviour : null;
        }
    }

    public Recorded? Find(string key)
    {
        lock (_gate)
        {
            return _operations.GetValueOrDefault(key);
        }
    }

    public IReadOnlyList<PaymentIntentView> Intents()
    {
        lock (_gate)
        {
            return [.. _intents.Values.OrderBy(static i => i.Created).ThenBy(static i => i.Id, StringComparer.Ordinal).Select(ViewOf)];
        }
    }

    public PaymentIntentView? FindIntent(string id)
    {
        lock (_gate)
        {
            return _intents.TryGetValue(id, out Intent? intent) ? ViewOf(intent) : null;
        }
    }

    public IReadOnlyList<RefundView> Refunds()
    {
        lock (_gate)
        {
            return [.. _refunds.Select(ViewOf)];
        }
    }

    public Execution Tokenise(string key, TokenRequest request)
    {
        CardInput? card = request.Card;
        // The fingerprint holds the last four digits and the expiry, never the number: the repeat of a request has the
        // same ones, and a different card under the same key almost surely differs in at least one.
        string fingerprint = card is null ? "token|" : $"token|{Last4(card.Number)}|{card.ExpMonth}|{card.ExpYear}";

        return Run(key, "token", fingerprint, () => TokeniseCore(card));
    }

    public Execution Authorise(string key, PaymentIntentRequest request)
    {
        string fingerprint = string.Join('|', "authorise", request.Amount, request.Currency, request.PaymentMethod);
        return Run(key, "authorise", fingerprint, () => AuthoriseCore(request));
    }

    public Execution Capture(string key, string intentId, CaptureRequest request)
    {
        string fingerprint = string.Join('|', "capture", intentId, request.AmountToCapture);
        return Run(key, "capture", fingerprint, () => CaptureCore(intentId, request));
    }

    public Execution Cancel(string key, string intentId) =>
        Run(key, "cancel", "cancel|" + intentId, () => CancelCore(intentId));

    public Execution Refund(string key, RefundRequest request)
    {
        string fingerprint = string.Join('|', "refund", request.PaymentIntent, request.Amount);
        return Run(key, "refund", fingerprint, () => RefundCore(request));
    }

    private Execution Run(string key, string type, string fingerprint, Func<Reply> perform)
    {
        lock (_gate)
        {
            if (_operations.TryGetValue(key, out Recorded? seen))
            {
                // The same key for another request is a bug in the caller, and answering it with the first request's
                // result would hide the bug. Stripe refuses it too.
                return seen.Type == type && seen.Fingerprint == fingerprint
                    ? new Execution(seen.Reply, Replayed: true)
                    : new Execution(Reply.Fail(ProviderError.KeyReused), Replayed: false);
            }

            Reply reply = perform();
            _operations[key] = new Recorded(key, type, fingerprint, reply, clock.GetUtcNow());
            return new Execution(reply, Replayed: false);
        }
    }

    private Reply TokeniseCore(CardInput? card)
    {
        if (card is null)
        {
            return Reply.Fail(ProviderError.Invalid("parameter_missing", "The card is required.", "card"));
        }

        if (!Cards.PassesLuhn(card.Number))
        {
            return Reply.Fail(ProviderError.Card("incorrect_number", "The card number is incorrect.", param: "card[number]"));
        }

        if (card.ExpMonth is < 1 or > 12)
        {
            return Reply.Fail(ProviderError.Card("invalid_expiry_month", "The card's expiration month is invalid.", param: "card[exp_month]"));
        }

        DateTimeOffset now = clock.GetUtcNow();
        if (card.ExpYear < now.Year || (card.ExpYear == now.Year && card.ExpMonth < now.Month))
        {
            return Reply.Fail(ProviderError.Card("expired_card", "The card has expired.", param: "card[exp_year]"));
        }

        if (card.Cvc is { } cvc && (cvc.Length is < 3 or > 4 || !cvc.All(char.IsAsciiDigit)))
        {
            return Reply.Fail(ProviderError.Card("invalid_cvc", "The card's security code is invalid.", param: "card[cvc]"));
        }

        if (Cards.Find(card.Number) is not { } testCard)
        {
            return Reply.Fail(ProviderError.Card("incorrect_number", "This is not one of the simulator's test cards.", param: "card[number]"));
        }

        var token = new Token(NewId("tok_"), testCard, Last4(card.Number), card.ExpMonth, card.ExpYear, now);
        _tokens[token.Id] = token;

        return Reply.Ok(
            new TokenView(token.Id, "token", new CardView(testCard.Brand, token.Last4, token.ExpMonth, token.ExpYear, testCard.Funding), now.ToUnixTimeSeconds()),
            SimJson.Default.TokenView);
    }

    private Reply AuthoriseCore(PaymentIntentRequest request)
    {
        if (request.Amount is < 1 or > MaxAmount)
        {
            return Reply.Fail(ProviderError.Invalid("parameter_invalid_integer", $"The amount must be between 1 and {MaxAmount.ToString(CultureInfo.InvariantCulture)}.", "amount"));
        }

        if (request.Currency is not { Length: 3 } currency || !currency.All(char.IsAsciiLetterLower))
        {
            return Reply.Fail(ProviderError.Invalid("parameter_invalid_enum", "The currency must be a three letter lower case code.", "currency"));
        }

        // Automatic capture would move money in one step, which is not the flow under test.
        if (request.CaptureMethod != "manual")
        {
            return Reply.Fail(ProviderError.Invalid("parameter_invalid_enum", "Only capture_method manual is simulated.", "capture_method"));
        }

        if (request.PaymentMethod is not { } tokenId || !_tokens.TryGetValue(tokenId, out Token? token))
        {
            return Reply.Fail(ProviderError.Missing("payment method", "payment_method"));
        }

        switch (token.Card.Behaviour)
        {
            case CardBehaviour.Decline:
                return Reply.Fail(ProviderError.Card("card_declined", "The card was declined.", "generic_decline", "payment_method"));
            case CardBehaviour.InsufficientFunds:
                return Reply.Fail(ProviderError.Card("card_declined", "The card has insufficient funds.", "insufficient_funds", "payment_method"));
        }

        var intent = new Intent(NewId("pi_"), request.Amount, currency, token, request.Metadata ?? [], clock.GetUtcNow());
        _intents[intent.Id] = intent;
        return Reply.Ok(ViewOf(intent), SimJson.Default.PaymentIntentView);
    }

    private Reply CaptureCore(string intentId, CaptureRequest request)
    {
        if (!_intents.TryGetValue(intentId, out Intent? intent))
        {
            return Reply.Fail(ProviderError.Missing("payment intent"));
        }

        if (intent.Status != IntentStatus.RequiresCapture)
        {
            return Reply.Fail(UnexpectedState(intent));
        }

        long amount = request.AmountToCapture ?? intent.Amount;
        if (amount < 1)
        {
            return Reply.Fail(ProviderError.Invalid("parameter_invalid_integer", "amount_to_capture must be positive.", "amount_to_capture"));
        }

        if (amount > intent.Amount)
        {
            return Reply.Fail(ProviderError.Invalid("amount_too_large", "amount_to_capture is more than the amount that was authorised.", "amount_to_capture"));
        }

        // Capturing less than was authorised releases the rest; the intent cannot be captured again.
        intent.Status = IntentStatus.Succeeded;
        intent.AmountReceived = amount;
        return Reply.Ok(ViewOf(intent), SimJson.Default.PaymentIntentView);
    }

    private Reply CancelCore(string intentId)
    {
        if (!_intents.TryGetValue(intentId, out Intent? intent))
        {
            return Reply.Fail(ProviderError.Missing("payment intent"));
        }

        if (intent.Status != IntentStatus.RequiresCapture)
        {
            return Reply.Fail(UnexpectedState(intent));
        }

        intent.Status = IntentStatus.Canceled;
        return Reply.Ok(ViewOf(intent), SimJson.Default.PaymentIntentView);
    }

    private Reply RefundCore(RefundRequest request)
    {
        if (request.PaymentIntent is not { } intentId || !_intents.TryGetValue(intentId, out Intent? intent))
        {
            return Reply.Fail(ProviderError.Missing("payment intent", "payment_intent"));
        }

        if (intent.Status != IntentStatus.Succeeded)
        {
            return Reply.Fail(UnexpectedState(intent));
        }

        long refundable = intent.AmountReceived - intent.AmountRefunded;
        long amount = request.Amount ?? refundable;
        if (amount < 1)
        {
            return Reply.Fail(ProviderError.Invalid("parameter_invalid_integer", "amount must be positive.", "amount"));
        }

        if (amount > refundable)
        {
            return Reply.Fail(ProviderError.Invalid("amount_too_large", "amount is more than what is left to refund on the payment intent.", "amount"));
        }

        intent.AmountRefunded += amount;
        var refund = new Refund(NewId("re_"), intent, amount, request.Metadata ?? [], clock.GetUtcNow());
        _refunds.Add(refund);
        return Reply.Ok(ViewOf(refund), SimJson.Default.RefundView);
    }

    private static ProviderError UnexpectedState(Intent intent) =>
        ProviderError.Invalid(
            "payment_intent_unexpected_state",
            $"The payment intent is {StatusName(intent.Status)}, and cannot take this operation.");

    private static PaymentIntentView ViewOf(Intent intent) => new(
        intent.Id,
        "payment_intent",
        intent.Amount,
        intent.Currency,
        StatusName(intent.Status),
        intent.Status == IntentStatus.RequiresCapture ? intent.Amount : 0,
        intent.AmountReceived,
        intent.AmountRefunded,
        intent.Token.Id,
        "manual",
        intent.Metadata,
        intent.Created.ToUnixTimeSeconds());

    private static RefundView ViewOf(Refund refund) => new(
        refund.Id,
        "refund",
        refund.Amount,
        refund.Intent.Currency,
        refund.Intent.Id,
        "succeeded",
        refund.Metadata,
        refund.Created.ToUnixTimeSeconds());

    private static string StatusName(IntentStatus status) => status switch
    {
        IntentStatus.RequiresCapture => "requires_capture",
        IntentStatus.Succeeded => "succeeded",
        _ => "canceled",
    };

    private static string Last4(string number) => number.Length >= 4 ? number[^4..] : number;

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..24];
}
