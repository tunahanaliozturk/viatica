namespace Viatica.Payments.Application.Provider;

// What the provider can answer, as values. A provider call has several normal endings (it did the work, it said no, it
// could not be reached, the answer was lost) and each one is a different next step for the caller, so each is a case of
// a union and the caller's switch is checked for completeness (ADR 0001). None of these ever goes on the wire: the API
// and the events map them explicitly.

public enum FailureKind
{
    Timeout,
    Error,
    CircuitOpen,
}

/// <summary>
/// The provider could not be asked, or did not answer usefully.
/// </summary>
/// <param name="Kind">What a log line and a metric name.</param>
/// <param name="MayHaveReachedProvider">
/// True when the request may have been processed even though no answer came back: a timeout, a dropped connection, a
/// 500. False when it certainly was not: the circuit was open and nothing was sent, or the provider said it was
/// unavailable (503) before doing anything. An operation looks the key up when this is true.
/// </param>
public sealed record ProviderFailure(FailureKind Kind, bool MayHaveReachedProvider);

/// <summary>The provider did it. <see cref="Reference"/> is its id for what it made: the payment intent, or the refund.</summary>
public sealed record ProviderSucceeded(string Reference);

/// <summary>The provider answered and said no. Nothing was done. <see cref="Code"/> is its decline or error code.</summary>
public sealed record ProviderRefused(string Code);

/// <summary>What one call to the provider came to.</summary>
public union ProviderResult(ProviderSucceeded, ProviderRefused, ProviderFailure);

/// <summary>An answer the provider gave, as opposed to a failure to get one.</summary>
public union ProviderAnswer(ProviderSucceeded, ProviderRefused);

/// <summary>The provider has an operation under that key, and this is what it answered.</summary>
public sealed record OperationFound(ProviderAnswer Answer);

/// <summary>The provider has no operation under that key: the request never reached it, or it has forgotten.</summary>
public sealed record NoSuchOperation;

public union LookupResult(OperationFound, NoSuchOperation, ProviderFailure);

/// <summary>The provider turned a card into a token. These are the facts about the card it is willing to tell us.</summary>
public sealed record Tokenised(string ProviderToken, string Brand, string Last4, int ExpMonth, int ExpYear);

/// <summary>The provider will not take this card. <see cref="Code"/> is its error code and never includes the card.</summary>
public sealed record TokenRefused(string Code);

public union TokeniseOutcome(Tokenised, TokenRefused, ProviderFailure);
