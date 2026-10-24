namespace Viatica.Stays.Domain;

/// <summary>
/// One room type on one night: how many rooms there are, how many are sold or held, and the price. This row is the
/// whole of the inventory model. <c>sold + held &lt;= allotment</c> is enforced by a <c>CHECK</c> constraint and by
/// the <c>WHERE</c> clause of every statement that reserves rooms, never by reading the row first.
/// </summary>
public sealed class RoomNight
{
    private RoomNight()
    {
    }

    public Guid RoomTypeId { get; private set; }

    public DateOnly Night { get; private set; }

    public int Allotment { get; private set; }

    public int Sold { get; private set; }

    public int Held { get; private set; }

    public decimal Rate { get; private set; }

    public int Available => Allotment - Sold - Held;

    public static RoomNight Open(Guid roomTypeId, DateOnly night, int allotment, decimal rate) =>
        new() { RoomTypeId = roomTypeId, Night = night, Allotment = allotment, Rate = rate };
}

/// <summary>
/// The counter behind <c>AvailabilityVersion</c> for one room type. It lives in a row of its own, not on the room
/// type, because every hold bumps it: on the room type it would change the row's <c>xmin</c> and with it the ETag a
/// partner is holding, so editing a room type would fail with 412 whenever a traveller booked.
/// </summary>
/// <remarks>
/// Writers of one room type queue on this row. That is the price of a version that is gapless and ordered like the
/// commits, which is what a projection needs to tell a stale update from a fresh one.
/// </remarks>
public sealed class RoomTypeInventory
{
    private RoomTypeInventory()
    {
    }

    public Guid RoomTypeId { get; private set; }

    public long AvailabilityVersion { get; private set; }

    public static RoomTypeInventory For(Guid roomTypeId) => new() { RoomTypeId = roomTypeId };
}
