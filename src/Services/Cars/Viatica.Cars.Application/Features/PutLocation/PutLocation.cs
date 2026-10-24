using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Cars.Domain;
using Viatica.Kernel;

namespace Viatica.Cars.Application.Features.PutLocation;

/// <summary>
/// Creates or replaces a rental location under an id the client chose, so a retry is the same request. A replace
/// carries the version the client last read; <c>null</c> means any version, which is also how a create is sent.
/// </summary>
public sealed record PutLocation(
    Guid PartnerId,
    Guid Id,
    uint? ExpectedVersion,
    string Name,
    Address Address,
    GeoPoint Position,
    string? AirportCode,
    IReadOnlyList<OpeningWindow> OpeningHours,
    CancellationPolicy CancellationPolicy) : IRequest<Versioned<LocationView>>;

public sealed class PutLocationValidator : AbstractValidator<PutLocation>
{
    public PutLocationValidator()
    {
        RuleFor(static c => !string.IsNullOrWhiteSpace(c.Name) && c.Name.Length <= 120, "name is required, at most 120 characters.", nameof(PutLocation.Name))
            .WithErrorCode("InvalidName");
        RuleFor(
            static c => !string.IsNullOrWhiteSpace(c.Address.Line1) && !string.IsNullOrWhiteSpace(c.Address.City)
                && !string.IsNullOrWhiteSpace(c.Address.PostalCode),
            "address needs a street line, a city and a postal code.",
            nameof(PutLocation.Address)).WithErrorCode("InvalidAddress");
        RuleFor(static c => c.Address.CountryCode is { Length: 2 } code && code.All(char.IsAsciiLetter), "countryCode is two letters.", "Address.CountryCode")
            .WithErrorCode("InvalidCountryCode");
        RuleFor(static c => c.Position.IsInRange(), "position is outside the globe.", nameof(PutLocation.Position))
            .WithErrorCode("InvalidPosition");
        RuleFor(static c => c.AirportCode is null || RentalLocation.IsIataCode(c.AirportCode), "airportCode is a three letter IATA code.", nameof(PutLocation.AirportCode))
            .WithErrorCode("InvalidAirportCode");
        RuleFor(static c => OpeningHoursAreValid(c.OpeningHours), "openingHours holds at most one window per weekday, each closing after it opens, in whole minutes.", nameof(PutLocation.OpeningHours))
            .WithErrorCode("InvalidOpeningHours");
        RuleFor(
            static c => c.CancellationPolicy.FreeUntilHoursBeforeStart is >= 0 and <= 8760
                && Enum.IsDefined(c.CancellationPolicy.PenaltyAfter),
            "cancellationPolicy needs 0 to 8760 free hours and a known penalty.",
            nameof(PutLocation.CancellationPolicy)).WithErrorCode("InvalidCancellationPolicy");
    }

    private static bool OpeningHoursAreValid(IReadOnlyList<OpeningWindow> windows) =>
        windows.Count <= 7
        && windows.Select(static w => w.Day).Distinct().Count() == windows.Count
        && windows.All(static w => Enum.IsDefined(w.Day) && w.Opens < w.Closes
            && w.Opens.Second == 0 && w.Closes.Second == 0 && w.Opens.Millisecond == 0 && w.Closes.Millisecond == 0);
}

public sealed class PutLocationHandler(ICarsDb db, TimeProvider clock) : IRequestHandler<PutLocation, Versioned<LocationView>>
{
    public async Task<Versioned<LocationView>> Handle(PutLocation request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        RentalLocation? location = await db.Locations.FirstOrDefaultAsync(l => l.Id == request.Id, cancellationToken);

        // Another partner's id looks exactly like an id nobody has used, so ids do not leak.
        if (location is not null && location.PartnerId != request.PartnerId)
        {
            throw CarErrors.LocationNotFound();
        }

        bool created = location is null;
        if (location is null)
        {
            if (request.ExpectedVersion is not null)
            {
                throw ApiErrorException.PreconditionFailed();
            }

            location = RentalLocation.Create(
                request.Id, request.PartnerId, request.Name, request.Address, request.Position, request.AirportCode,
                request.OpeningHours, request.CancellationPolicy, now);
            db.Locations.Add(location);
        }
        else
        {
            if (request.ExpectedVersion is { } expected && expected != location.Version)
            {
                throw ApiErrorException.PreconditionFailed();
            }

            location.Revise(
                request.Name, request.Address, request.Position, request.AirportCode, request.OpeningHours,
                request.CancellationPolicy, now);
        }

        return await db.InTransactionAsync(
            async transaction =>
            {
                if (location.Status == ListingStatus.Published)
                {
                    await Announcements.ListingAsync(db, location, now, cancellationToken);
                }

                await db.SaveChangesAsync(cancellationToken);
                return new Versioned<LocationView>(LocationView.From(location), location.Version, created);
            },
            cancellationToken);
    }
}
