using System.Security.Cryptography;

namespace Viatica.AirlineSim.Orders;

/// <summary>
/// Mints booking references. Two random six character codes collide about once in a billion draws, so a test that
/// wants to see what order creation does about it needs to choose the codes.
/// </summary>
internal interface IBookingReferences
{
    string Next();
}

/// <summary>Six characters from an alphabet without the look-alikes (no 0, 1, I or O).</summary>
internal sealed class RandomBookingReferences : IBookingReferences
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string Next() => RandomNumberGenerator.GetString(Alphabet, 6);
}
