using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Stays.Application.Inventory;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Holds;

public sealed record HoldPlaced(Hold Hold, HoldTerms Terms);

public sealed record SoldOut;

public sealed record NotBookable;

public sealed record QuoteExpired;

public sealed record QuoteInvalid;

/// <summary>
/// What asking for a hold can come to. Every case is a normal answer that Booking is told about, not an error, so
/// they are a union and not exceptions (ADR 0001). The union stays inside the service: the wire carries
/// <see cref="StayHoldPlaced"/> or <see cref="StayHoldRejected"/>.
/// </summary>
public union HoldOutcome(HoldPlaced, SoldOut, NotBookable, QuoteExpired, QuoteInvalid);

/// <summary>What the property said when the hold was placed: whose it is, when check-in opens, and the cancellation policy.</summary>
internal sealed record PropertyTerms(
    PropertyStatus Status, Guid PartnerId, TimeOnly CheckInFrom, int FreeCancellationHours, Domain.CancellationPenalty Penalty)
{
    /// <summary>
    /// Properties have no time zone yet, so check-in is the date at the property's check-in time, read as UTC. Booking
    /// uses it to decide whether a cancellation is still free.
    /// </summary>
    public HoldTerms ToTerms(DateOnly checkIn) => new(
        PartnerId,
        new DateTimeOffset(checkIn.ToDateTime(CheckInFrom), TimeSpan.Zero),
        new Contracts.CancellationPolicy(
            FreeCancellationHours,
            Penalty switch
            {
                Domain.CancellationPenalty.FirstUnit => Contracts.CancellationPenalty.FirstUnit,
                Domain.CancellationPenalty.FullAmount => Contracts.CancellationPenalty.FullAmount,
                _ => throw new InvalidOperationException("Unknown cancellation penalty."),
            }));
}

/// <summary>
/// Booking asks for a hold with a quote token. The token is checked, then every night of the stay is reserved in one
/// statement or none is, and the answer is queued for Booking in the same transaction as the hold and the inbox row.
/// </summary>
public sealed class PlaceHoldHandler(
    IStaysDb db, NightLedger ledger, QuoteTokens tokens, TimeProvider clock, ILogger<PlaceHoldHandler> logger)
    : IIntegrationEventHandler<StayHoldRequested>
{
    public async Task HandleAsync(StayHoldRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using Activity? activity = StaysTelemetry.Source.StartActivity("place hold");

        // The hold's id is Booking's, so a request sent twice (a restart, a resend with a new event id) finds the hold
        // it already made and does nothing more. The inbox covers the same event id; this covers a new one.
        if (await db.Holds.AnyAsync(h => h.Id == message.HoldId, cancellationToken))
        {
            logger.HoldAlreadyPlaced(message.HoldId);
            return;
        }

        HoldOutcome outcome = await PlaceAsync(message, cancellationToken);

        switch (outcome)
        {
            case HoldPlaced placed:
                db.Publish(new StayHoldPlaced(
                    placed.Hold.Id, placed.Hold.TripId, placed.Hold.ItemId, placed.Hold.PropertyId, placed.Hold.RoomTypeId,
                    placed.Hold.CheckIn, placed.Hold.CheckOut, placed.Hold.Quantity,
                    new Price(placed.Hold.Total, Money.Euro), placed.Hold.ExpiresAt, placed.Terms));
                StaysTelemetry.HoldsPlaced.Add(1);
                break;

            case SoldOut:
                Reject(message, HoldRejectionReason.SoldOut);
                break;

            case NotBookable:
                Reject(message, HoldRejectionReason.NotBookable);
                break;

            case QuoteExpired:
                Reject(message, HoldRejectionReason.QuoteExpired);
                break;

            case QuoteInvalid:
                Reject(message, HoldRejectionReason.QuoteInvalid);
                break;

            // A union can be default, holding no case. Nothing here returns one, so reaching this is a bug.
            case null:
                throw new UnreachableException("Placing a hold returned no outcome.");
        }
    }

    private async Task<HoldOutcome> PlaceAsync(StayHoldRequested request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();

        if (!tokens.TryRead(request.QuoteToken, now, out QuotePayload? quote, out QuoteFailure failure))
        {
            return failure == QuoteFailure.Expired ? new QuoteExpired() : new QuoteInvalid();
        }

        // Booking says how many rooms it wants and the token says how many it priced. A request that asks for more
        // than the token paid for would book rooms nobody quoted.
        if (quote!.Quantity != request.Quantity || quote.Total.Currency != Money.Euro)
        {
            return new QuoteInvalid();
        }

        Guid roomTypeId = quote.RoomTypeId;
        Guid propertyId = quote.PropertyId;
        PropertyTerms? property = await db.RoomTypes.AsNoTracking()
            .Where(r => r.Id == roomTypeId && r.PropertyId == propertyId)
            .Select(static r => r.Property == null
                ? null
                : new PropertyTerms(
                    r.Property.Status, r.Property.PartnerId, r.Property.CheckInFrom,
                    r.Property.FreeCancellationHours, r.Property.PenaltyAfterFreeCancellation))
            .SingleOrDefaultAsync(cancellationToken);
        if (property is not { Status: PropertyStatus.Published })
        {
            return new NotBookable();
        }

        if (!await ledger.TryReserveAsync(roomTypeId, quote.Dates, request.Quantity, cancellationToken))
        {
            return new SoldOut();
        }

        // The price is the one the quote pinned, not whatever the rates say now: that is what "pins the price" means,
        // and it is the total Booking will ask Payments to authorise.
        Hold hold = Hold.Place(
            request.HoldId, request.TripId, request.ItemId, propertyId, roomTypeId,
            quote.Dates, request.Quantity, quote.Total.Amount, now, Hold.Lifetime);
        db.Holds.Add(hold);

        await ledger.PublishAvailabilityAsync(hold, cancellationToken);
        return new HoldPlaced(hold, property.ToTerms(quote.Dates.CheckIn));
    }

    private void Reject(StayHoldRequested request, HoldRejectionReason reason)
    {
        db.Publish(new StayHoldRejected(request.HoldId, request.TripId, request.ItemId, reason));
        StaysTelemetry.HoldsRejected.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));
        logger.HoldRejected(request.HoldId, reason);
    }
}
