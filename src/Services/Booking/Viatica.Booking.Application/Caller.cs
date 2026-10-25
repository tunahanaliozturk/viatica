using System.Security.Cryptography;
using System.Text;
using Viatica.Booking.Domain;

namespace Viatica.Booking.Application;

/// <summary>
/// Who is asking, as far as the use cases care. The endpoint builds it from the token and the
/// <c>Trip-Access-Key</c> header, so Application never sees ASP.NET Core.
/// </summary>
/// <param name="TravellerId">The signed-in traveller, or null.</param>
/// <param name="IsAdmin">An admin may read any trip and change none.</param>
/// <param name="AccessKey">The secret that proves the holder built an anonymous trip.</param>
public sealed record Caller(Guid? TravellerId, bool IsAdmin, string? AccessKey)
{
    public const int MinimumAccessKeyLength = 22;

    public static Caller Anonymous { get; } = new(null, false, null);

    /// <summary>
    /// May act on this trip. A signed-in trip belongs to its traveller and to nobody else; an anonymous one belongs to
    /// whoever holds its key. Anything else is "not found", so an id says nothing about whose trip it is.
    /// </summary>
    public bool Owns(Trip trip)
    {
        ArgumentNullException.ThrowIfNull(trip);

        return trip.TravellerId is { } owner
            ? TravellerId == owner
            : AccessKey is { Length: >= MinimumAccessKeyLength } && trip.AccessKeyMatches(HashOf(AccessKey));
    }

    public bool CanRead(Trip trip) => IsAdmin || Owns(trip);

    public static byte[] HashOf(string accessKey) => SHA256.HashData(Encoding.UTF8.GetBytes(accessKey));
}
