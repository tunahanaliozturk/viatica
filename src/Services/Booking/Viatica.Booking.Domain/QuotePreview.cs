using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using Viatica.Kernel;

namespace Viatica.Booking.Domain;

/// <summary>
/// What a quote token says, read without checking it. The payload is readable on purpose (docs/design.md, "Quote
/// tokens") so a trip can show what is in it and what it should cost. Nothing here is trusted: the owning service
/// checks the signature and the expiry when it places the hold, and the hold's total is the one that is charged.
/// </summary>
public sealed record QuotePreview(
    ItemKind Kind,
    string Description,
    Money Total,
    DateTimeOffset ExpiresAt,
    int Quantity,
    int? DriverAge)
{
    private const int MaxTokenLength = 2048;

    /// <summary>False for anything that is not a stay or car quote in the shape the owners issue.</summary>
    public static bool TryRead(string? token, out QuotePreview? preview)
    {
        preview = null;

        // The signature half is ignored, so the whole length is checked here: nothing bigger than a real token is decoded.
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        int dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot != token.LastIndexOf('.') || !Base64Url.IsValid(token.AsSpan(0, dot)))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(Base64Url.DecodeFromChars(token.AsSpan(0, dot)));
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("v").GetInt32() != 1)
            {
                return false;
            }

            JsonElement total = root.GetProperty("total");
            var money = new Money(
                decimal.Parse(total.GetProperty("amount").GetString()!, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
                total.GetProperty("currency").GetString()!);
            DateTimeOffset expiresAt = root.GetProperty("expiresAt").GetDateTimeOffset();

            switch (root.GetProperty("kind").GetString())
            {
                case "stay":
                    DateOnly checkIn = DateOnly.ParseExact(root.GetProperty("checkIn").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    DateOnly checkOut = DateOnly.ParseExact(root.GetProperty("checkOut").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    int rooms = root.GetProperty("quantity").GetInt32();
                    if (rooms < 1 || checkOut <= checkIn)
                    {
                        return false;
                    }

                    preview = new QuotePreview(
                        ItemKind.Stay,
                        string.Create(CultureInfo.InvariantCulture, $"Stay {checkIn:yyyy-MM-dd} to {checkOut:yyyy-MM-dd}, {rooms} room(s)"),
                        money, expiresAt, rooms, null);
                    return true;

                case "car":
                    DateTimeOffset pickUp = root.GetProperty("pickUpAt").GetDateTimeOffset();
                    DateTimeOffset dropOff = root.GetProperty("dropOffAt").GetDateTimeOffset();
                    int age = root.GetProperty("driverAge").GetInt32();
                    if (dropOff <= pickUp || age < 16)
                    {
                        return false;
                    }

                    preview = new QuotePreview(
                        ItemKind.Car,
                        string.Create(CultureInfo.InvariantCulture, $"Car rental {pickUp:yyyy-MM-dd HH:mm} to {dropOff:yyyy-MM-dd HH:mm}"),
                        money, expiresAt, 1, age);
                    return true;

                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException
            or ArgumentException or OverflowException)
        {
            // Not a token we know how to read. The text can quote the payload, so it is not kept.
            return false;
        }
    }
}
