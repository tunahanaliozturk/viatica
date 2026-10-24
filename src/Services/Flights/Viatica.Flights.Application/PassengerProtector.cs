using System.Globalization;
using Viatica.Kernel.Pii;

namespace Viatica.Flights.Application;

/// <summary>
/// The two places a passenger list is sealed. The broker key (<c>Viatica:Pii:PassengerKey</c>) is shared with
/// Booking and protects the list in transit; the storage key (<c>Viatica:Pii:StorageKey</c>) is Flights' own and
/// protects it at rest. Two keys so that rotating or leaking one does not expose or strand the other.
/// </summary>
public sealed class PassengerProtector(PiiSealer broker, PiiSealer storage)
{
    public bool TryOpenFromBroker(string sealedValue, Guid tripId, Guid itemId, out IReadOnlyList<PassengerDetails> passengers) =>
        broker.TryOpenPassengers(sealedValue, tripId, itemId, out passengers);

    public string SealForStorage(IReadOnlyList<PassengerDetails> passengers, Guid itemId) =>
        storage.SealPassengers(passengers, StorageAssociatedData(itemId));

    public bool TryOpenFromStorage(string sealedValue, Guid itemId, out IReadOnlyList<PassengerDetails> passengers) =>
        storage.TryOpenPassengers(sealedValue, StorageAssociatedData(itemId), out passengers);

    // Bound to the item, so a stored list copied onto another order's row does not open.
    private static string StorageAssociatedData(Guid itemId) =>
        string.Create(CultureInfo.InvariantCulture, $"flight-order:{itemId:N}");
}
