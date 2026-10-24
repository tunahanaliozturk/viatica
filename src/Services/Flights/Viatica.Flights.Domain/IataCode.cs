using Moongazing.OrionGuard.Core;

namespace Viatica.Flights.Domain;

/// <summary>An airport code: three upper case letters, as printed on a boarding pass.</summary>
public sealed record IataCode
{
    public IataCode(string value)
    {
        Ensure.That(value, nameof(value)).NotNull().Matches("^[A-Z]{3}$");
        Value = value;
    }

    public string Value { get; }

    public static bool IsValid(string? value) =>
        value is { Length: 3 } && value.All(static c => c is >= 'A' and <= 'Z');

    public override string ToString() => Value;
}
