using System.Globalization;
using Viatica.Booking.Domain;
using Viatica.Kernel;

namespace Viatica.Booking.Application;

/// <summary>A trip as the traveller sees it. Never carries a name, an email or a payment token.</summary>
public sealed record TripView(
    Guid Id,
    TripStatus Status,
    bool Claimed,
    Money Total,
    Money Refunded,
    IReadOnlyList<TripItemView> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Revision,
    uint Version,
    FailureReason? Failure);

public sealed record TripItemView(
    Guid Id,
    ItemKind Kind,
    ItemStatus Status,
    string Description,
    Money Total,
    DateTimeOffset? StartsAt,
    DateTimeOffset? FreeCancellationUntil,
    CancelState Cancellation,
    Money? Refund,
    RefundStatus RefundStatus,
    string? Reason);

public static class Views
{
    public static TripView Of(Trip trip)
    {
        ArgumentNullException.ThrowIfNull(trip);

        return new TripView(
            trip.Id,
            trip.Status,
            trip.TravellerId is not null,
            trip.CurrentTotal,
            trip.RefundedTotal,
            [.. trip.Items.OrderBy(static i => i.AddedAt).ThenBy(static i => i.Id).Select(Of)],
            trip.CreatedAt,
            trip.UpdatedAt,
            trip.Revision,
            trip.Version,
            trip.Failure);
    }

    public static TripItemView Of(TripItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new TripItemView(
            item.Id,
            item.Kind,
            item.Status,
            item.Description,
            item.Total ?? item.ExpectedTotal,
            item.StartsAt,
            item.StartsAt is { } starts && item.FreeCancellationHours is { } hours ? starts - TimeSpan.FromHours(hours) : null,
            item.Cancel,
            item.RefundAmount is { } refund ? new Money(refund, item.Currency) : null,
            item.Refund,
            item.Refusal is { Length: > 0 } reason ? reason[..Math.Min(reason.Length, 120)] : null);
    }

    /// <summary>The state of a checkout as a long running operation (docs/api/conventions.md).</summary>
    public static Operation CheckoutOperation(Trip trip)
    {
        ArgumentNullException.ThrowIfNull(trip);
        string id = trip.Id.ToString("D", CultureInfo.InvariantCulture);

        return trip.Status switch
        {
            TripStatus.Draft => new Operation(id, OperationStatus.NotStarted),
            TripStatus.Confirmed or TripStatus.Cancelled =>
                new Operation(id, OperationStatus.Succeeded) { ResourceLocation = $"/trips/{id}" },
            TripStatus.Failed => new Operation(id, OperationStatus.Failed)
            {
                Error = new ApiError(trip.Failure?.ToString() ?? nameof(FailureReason.ItemRejected), FailureMessage(trip.Failure)),
                ResourceLocation = $"/trips/{id}",
            },
            _ => new Operation(id, OperationStatus.Running),
        };
    }

    public static Operation CancellationOperation(Trip trip, TripItem item)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(item);
        string id = item.Id.ToString("D", CultureInfo.InvariantCulture);

        if (item.Status == ItemStatus.Cancelled)
        {
            return new Operation(id, OperationStatus.Succeeded) { ResourceLocation = $"/trips/{trip.Id:D}" };
        }

        return item.Cancel switch
        {
            CancelState.Requested => new Operation(id, OperationStatus.Running),
            CancelState.TimedOut => new Operation(id, OperationStatus.Failed)
            {
                Error = new ApiError("CancellationTimedOut", "The cancellation was not confirmed in time. Ask again."),
                ResourceLocation = $"/trips/{trip.Id:D}",
            },
            _ => new Operation(id, OperationStatus.NotStarted),
        };
    }

    private static string FailureMessage(FailureReason? reason) => reason switch
    {
        FailureReason.ItemRejected => "An item could not be reserved, so nothing was booked and nothing was charged.",
        FailureReason.PaymentDeclined => "The payment was declined, so nothing was booked and nothing was charged.",
        FailureReason.TicketingRefused => "The airline would not issue tickets, so nothing was booked and nothing was charged.",
        FailureReason.DeadlineReached => "The reservations were about to expire before the booking finished, so nothing was booked and nothing was charged.",
        _ => "The checkout failed, so nothing was booked and nothing was charged.",
    };
}
