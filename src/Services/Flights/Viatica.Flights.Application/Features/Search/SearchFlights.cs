using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Flights.Application.Offers;
using Viatica.Flights.Application.Suppliers;
using Viatica.Flights.Domain;

namespace Viatica.Flights.Application.Features.Search;

/// <summary>Search one way or return flights. Strings stay strings until the validator has looked at them.</summary>
public sealed record SearchFlightsCommand(
    string Origin,
    string Destination,
    DateOnly DepartureDate,
    DateOnly? ReturnDate,
    int Adults,
    int Children,
    int Infants,
    Cabin Cabin) : IRequest<SearchFlightsResult>
{
    internal SearchCriteria ToCriteria() => new(
        new IataCode(Origin),
        new IataCode(Destination),
        DepartureDate,
        ReturnDate,
        new PassengerMix(Adults, Children, Infants),
        Cabin);
}

/// <summary>The first page of a new search, or the news that no airline answered at all.</summary>
public union SearchFlightsResult(FlightSearchPage, NoSuppliersAnswered);

/// <summary>Every airline failed, timed out or was skipped. There is nothing to cache and nothing to page.</summary>
public sealed record NoSuppliersAnswered(IReadOnlyList<IncompleteSupplier> IncompleteSuppliers);

public sealed class SearchFlightsValidator : AbstractValidator<SearchFlightsCommand>
{
    public SearchFlightsValidator(TimeProvider time, FlightsOptions options)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);

        // The calendar day in UTC. A traveller one time zone ahead can search for "today" a few hours early or late,
        // and the airline has its own two hour cut-off anyway, so the edge is the airline's to enforce.
        DateOnly Today() => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        RuleFor(static c => IataCode.IsValid(c.Origin), "origin must be a three letter upper case airport code.", nameof(SearchFlightsCommand.Origin))
            .WithErrorCode("InvalidAirport");
        RuleFor(static c => IataCode.IsValid(c.Destination), "destination must be a three letter upper case airport code.", nameof(SearchFlightsCommand.Destination))
            .WithErrorCode("InvalidAirport");
        RuleFor(static c => !string.Equals(c.Origin, c.Destination, StringComparison.Ordinal), "destination must differ from origin.", nameof(SearchFlightsCommand.Destination))
            .WithErrorCode("OriginEqualsDestination");

        RuleFor(c => c.DepartureDate >= Today(), "departureDate cannot be in the past.", nameof(SearchFlightsCommand.DepartureDate))
            .WithErrorCode("DepartureInPast");
        RuleFor(c => c.DepartureDate <= Today().AddDays(options.BookingHorizonDays), $"departureDate cannot be more than {options.BookingHorizonDays} days ahead.", nameof(SearchFlightsCommand.DepartureDate))
            .WithErrorCode("DepartureTooFar");
        RuleFor(static c => c.ReturnDate is null || c.ReturnDate >= c.DepartureDate, "returnDate cannot be before departureDate.", nameof(SearchFlightsCommand.ReturnDate))
            .WithErrorCode("ReturnBeforeDeparture");
        RuleFor(c => c.ReturnDate is null || c.ReturnDate <= Today().AddDays(options.BookingHorizonDays), $"returnDate cannot be more than {options.BookingHorizonDays} days ahead.", nameof(SearchFlightsCommand.ReturnDate))
            .WithErrorCode("DepartureTooFar");

        RuleFor(static c => Enum.IsDefined(c.Cabin), "cabin must be Economy or Business.", nameof(SearchFlightsCommand.Cabin))
            .WithErrorCode("InvalidCabin");

        // The mix rules live in the domain; each failure keeps the domain's code and names the field it is about.
        foreach (MixProblem problem in MixProblems.All)
        {
            RuleFor(
                    c => PassengerMix.Check(c.Adults, c.Children, c.Infants) is not { } found || found.Code != problem.Code,
                    problem.Message,
                    problem.Target)
                .WithErrorCode(problem.Code);
        }
    }
}
