using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Kernel;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Quotes;

/// <summary>Asks what a stay costs. Anyone may ask; the answer is only as good as the token that comes with it.</summary>
public sealed record CreateQuote(Guid PropertyId, Guid RoomTypeId, DateOnly CheckIn, DateOnly CheckOut, int Quantity, int Guests)
    : IRequest<Quote>;

/// <param name="Nights">One line per night. <c>Rate</c> is per room; the total is the sum times <c>Quantity</c>.</param>
/// <param name="QuoteToken">Pins the price for fifteen minutes; Booking hands it back when it asks for a hold.</param>
public sealed record Quote(
    Guid PropertyId,
    Guid RoomTypeId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Quantity,
    IReadOnlyList<QuoteNight> Nights,
    Money Total,
    string QuoteToken,
    DateTimeOffset ExpiresAt);

public sealed record QuoteNight(DateOnly Date, Money Rate);

/// <summary>The stay rules that need no database: order, length, how many rooms, how many guests.</summary>
public sealed class CreateQuoteValidator : AbstractValidator<CreateQuote>
{
    public CreateQuoteValidator(TimeProvider clock)
    {
        RuleFor(static q => q.CheckOut > q.CheckIn, "checkOut must be after checkIn.", nameof(CreateQuote.CheckOut))
            .WithErrorCode(StayErrorCode.CheckOutBeforeCheckIn);

        // A reversed range would give a negative length, which is "not too long"; the rule above reports it.
        RuleFor(static q => q.CheckOut.DayNumber - q.CheckIn.DayNumber <= StayDates.MaxNights, $"a stay is at most {StayDates.MaxNights} nights.", nameof(CreateQuote.CheckOut))
            .WithErrorCode(StayErrorCode.StayTooLong);

        RuleFor(q => q.CheckIn >= StayDates.EarliestCheckIn(clock.GetUtcNow()), "checkIn is in the past.", nameof(CreateQuote.CheckIn))
            .WithErrorCode(StayErrorCode.CheckInInPast);

        RuleFor(static q => q.Quantity is >= 1 and <= 9, "quantity must be between 1 and 9 rooms.", nameof(CreateQuote.Quantity));
        RuleFor(static q => q.Guests is >= 1 and <= 100, "guests must be between 1 and 100.", nameof(CreateQuote.Guests));
    }
}

public sealed class CreateQuoteHandler(IStaysDb db, QuoteTokens tokens, TimeProvider clock) : IRequestHandler<CreateQuote, Quote>
{
    public async Task<Quote> Handle(CreateQuote request, CancellationToken cancellationToken)
    {
        StayProperty property = await db.Properties.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken)
            ?? throw StayErrors.PropertyNotFound();

        // Say so plainly to a traveller holding a stale link, rather than pretending the property never existed.
        if (!property.IsPublished)
        {
            throw StayErrors.PropertyNotPublished();
        }

        RoomType roomType = await db.RoomTypes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.RoomTypeId && r.PropertyId == property.Id, cancellationToken)
            ?? throw StayErrors.RoomTypeNotFound();
        roomType.EnsureFits(request.Guests, request.Quantity);

        var dates = new StayDates(request.CheckIn, request.CheckOut);
        Guid roomTypeId = roomType.Id;
        DateOnly from = dates.CheckIn;
        DateOnly to = dates.CheckOut;
        int quantity = request.Quantity;

        // One read for the whole stay. A night with no row, or with fewer free rooms than asked for, is a night the
        // stay cannot have, so the quote is refused rather than priced for fewer nights than were requested.
        var nights = await db.RoomNights.AsNoTracking()
            .Where(n => n.RoomTypeId == roomTypeId && n.Night >= from && n.Night < to && n.Allotment - n.Sold - n.Held >= quantity)
            .OrderBy(static n => n.Night)
            .Select(static n => new { n.Night, n.Rate })
            .ToListAsync(cancellationToken);
        if (nights.Count != dates.Nights)
        {
            throw StayErrors.SoldOut();
        }

        QuoteNight[] lines = [.. nights.Select(static n => new QuoteNight(n.Night, Money.Eur(n.Rate)))];
        Money total = Money.Eur(nights.Sum(static n => n.Rate) * quantity);
        DateTimeOffset expiresAt = clock.GetUtcNow() + QuoteTokens.Lifetime;

        string token = tokens.Issue(new QuotePayload(
            QuotePayload.CurrentVersion, QuotePayload.StayKind, property.Id, roomType.Id,
            dates.CheckIn, dates.CheckOut, quantity, total, expiresAt));

        StaysTelemetry.QuotesIssued.Add(1);
        return new Quote(property.Id, roomType.Id, dates.CheckIn, dates.CheckOut, quantity, lines, total, token, expiresAt);
    }
}
