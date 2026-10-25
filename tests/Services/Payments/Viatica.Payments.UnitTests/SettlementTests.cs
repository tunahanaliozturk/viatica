using Microsoft.Extensions.Logging.Abstractions;
using Viatica.Kernel;
using Viatica.Payments.Application.Operations;
using Viatica.Payments.Application.Provider;
using Viatica.Payments.Domain;
using Viatica.Payments.Domain.Cards;

namespace Viatica.Payments.UnitTests;

/// <summary>
/// PAY-3 at the level where it is decided: what is done with a provider call that got no answer. Every test counts the
/// calls that reached the provider, because "never retry blindly" is a statement about calls.
/// </summary>
public sealed class SettlementTests
{
    private const string Key = "trip:capture:op";

    private static readonly ProviderFailure Timeout = new(FailureKind.Timeout, MayHaveReachedProvider: true);
    private static readonly ProviderFailure Dropped = new(FailureKind.Error, MayHaveReachedProvider: true);
    private static readonly ProviderFailure CircuitOpen = new(FailureKind.CircuitOpen, MayHaveReachedProvider: false);

    private readonly ScriptedProvider provider = new();
    private readonly ProviderSettlement settlement;

    public SettlementTests() => settlement = new ProviderSettlement(provider, NullLogger<ProviderSettlement>.Instance);

    private Task<Resolution> ResolveAsync() =>
        settlement.ResolveAsync(
            OperationKind.Capture, Guid.CreateVersion7(), Key, ct => provider.CaptureAsync("pi_1", Money.Eur(10m), Key, ct), CancellationToken.None);

    [Fact]
    public async Task An_answered_call_is_taken_at_its_word_with_no_lookup()
    {
        provider.Calls.Enqueue(new ProviderSucceeded("pi_1"));

        Resolved resolved = (await ResolveAsync()).Case<Resolved>();

        resolved.Answer.Case<ProviderSucceeded>().Reference.ShouldBe("pi_1");
        resolved.ByLookup.ShouldBeFalse();
        provider.CallCount.ShouldBe(1);
        provider.LookupCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_refusal_is_an_answer_too_and_is_not_looked_up_or_repeated()
    {
        provider.Calls.Enqueue(new ProviderRefused("amount_too_large"));

        Resolved resolved = (await ResolveAsync()).Case<Resolved>();

        resolved.Answer.Case<ProviderRefused>().Code.ShouldBe("amount_too_large");
        provider.CallCount.ShouldBe(1);
        provider.LookupCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_provider_that_was_certainly_not_reached_is_reported_as_nothing_sent_without_a_lookup()
    {
        provider.Calls.Enqueue(CircuitOpen);

        (await ResolveAsync()).Case<NothingSent>();

        provider.CallCount.ShouldBe(1);
        provider.LookupCount.ShouldBe(0, "nothing was sent, so there is nothing to look up");
    }

    [Theory]
    [InlineData(FailureKind.Timeout)]
    [InlineData(FailureKind.Error)]
    public async Task A_timeout_that_did_happen_is_found_by_the_lookup_and_the_call_is_not_repeated(FailureKind kind)
    {
        provider.Calls.Enqueue(new ProviderFailure(kind, MayHaveReachedProvider: true));
        provider.Lookups.Enqueue(new OperationFound(new ProviderSucceeded("pi_1")));

        Resolved resolved = (await ResolveAsync()).Case<Resolved>();

        resolved.Answer.Case<ProviderSucceeded>();
        resolved.ByLookup.ShouldBeTrue();
        provider.CallCount.ShouldBe(1, "a call that already happened must not be made again");
        provider.LookupCount.ShouldBe(1);
        provider.LookedUpKeys.ShouldBe([Key]);
    }

    [Fact]
    public async Task A_timeout_that_happened_as_a_refusal_ends_as_that_refusal()
    {
        provider.Calls.Enqueue(Timeout);
        provider.Lookups.Enqueue(new OperationFound(new ProviderRefused("card_declined")));

        Resolved resolved = (await ResolveAsync()).Case<Resolved>();

        resolved.Answer.Case<ProviderRefused>().Code.ShouldBe("card_declined");
        provider.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_timeout_that_did_not_happen_is_sent_again_once_under_the_same_key()
    {
        provider.Calls.Enqueue(Timeout);
        provider.Lookups.Enqueue(new NoSuchOperation());
        provider.Calls.Enqueue(new ProviderSucceeded("pi_1"));

        Resolved resolved = (await ResolveAsync()).Case<Resolved>();

        resolved.Answer.Case<ProviderSucceeded>();
        resolved.ByLookup.ShouldBeFalse();
        provider.CallCount.ShouldBe(2);
        provider.CallKeys.ShouldBe([Key, Key], "the same operation, the same key");
        provider.LookupCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_second_call_that_fails_too_settles_nothing_and_is_not_followed_by_a_third()
    {
        provider.Calls.Enqueue(Timeout);
        provider.Lookups.Enqueue(new NoSuchOperation());
        provider.Calls.Enqueue(Dropped);

        await Should.ThrowAsync<ProviderUnavailableException>(ResolveAsync);

        provider.CallCount.ShouldBe(2, "one resend, never a loop");
        provider.LookupCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_second_call_that_certainly_was_not_sent_still_decides_nothing_because_the_first_may_still_land()
    {
        provider.Calls.Enqueue(Timeout);
        provider.Lookups.Enqueue(new NoSuchOperation());
        provider.Calls.Enqueue(CircuitOpen);

        await Should.ThrowAsync<ProviderUnavailableException>(ResolveAsync);
    }

    [Fact]
    public async Task A_lookup_that_fails_too_is_not_read_as_no_such_operation_and_nothing_is_sent_again()
    {
        provider.Calls.Enqueue(Timeout);
        provider.Lookups.Enqueue(Timeout);

        await Should.ThrowAsync<ProviderUnavailableException>(ResolveAsync);

        provider.CallCount.ShouldBe(1, "a guess that it did not happen would repeat a call that may have");
        provider.LookupCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_second_call_that_is_refused_ends_as_the_refusal()
    {
        provider.Calls.Enqueue(Timeout);
        provider.Lookups.Enqueue(new NoSuchOperation());
        provider.Calls.Enqueue(new ProviderRefused("card_declined"));

        Resolved resolved = (await ResolveAsync()).Case<Resolved>();

        resolved.Answer.Case<ProviderRefused>().Code.ShouldBe("card_declined");
        provider.CallCount.ShouldBe(2);
    }
}

/// <summary>A provider that answers from queues and counts what reaches it. An empty queue is a test bug and throws.</summary>
internal sealed class ScriptedProvider : IPaymentProvider
{
    public Queue<ProviderResult> Calls { get; } = [];

    public Queue<LookupResult> Lookups { get; } = [];

    public List<string> CallKeys { get; } = [];

    public List<string> LookedUpKeys { get; } = [];

    public int CallCount => CallKeys.Count;

    public int LookupCount => LookedUpKeys.Count;

    public Task<TokeniseOutcome> TokeniseAsync(CardDetails card, string idempotencyKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ProviderResult> AuthoriseAsync(
        string providerToken, Money amount, Guid tripId, Guid paymentId, string idempotencyKey, CancellationToken cancellationToken) =>
        Next(idempotencyKey);

    public Task<ProviderResult> CaptureAsync(string intentId, Money amount, string idempotencyKey, CancellationToken cancellationToken) =>
        Next(idempotencyKey);

    public Task<ProviderResult> VoidAsync(string intentId, string idempotencyKey, CancellationToken cancellationToken) =>
        Next(idempotencyKey);

    public Task<ProviderResult> RefundAsync(
        string intentId, Money amount, Guid tripId, Guid refundId, string idempotencyKey, CancellationToken cancellationToken) =>
        Next(idempotencyKey);

    public Task<LookupResult> LookUpAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        LookedUpKeys.Add(idempotencyKey);
        return Task.FromResult(Lookups.Dequeue());
    }

    private Task<ProviderResult> Next(string key)
    {
        CallKeys.Add(key);
        return Task.FromResult(Calls.Dequeue());
    }
}
