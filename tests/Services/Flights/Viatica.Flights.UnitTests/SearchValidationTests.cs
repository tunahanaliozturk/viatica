using Microsoft.Extensions.Time.Testing;
using Moongazing.OrionGuard.Core;
using Viatica.Flights.Application;
using Viatica.Flights.Application.Features.Search;
using Viatica.Flights.Domain;

namespace Viatica.Flights.UnitTests;

public sealed class SearchValidationTests
{
    private static readonly DateOnly Today = new(2026, 11, 2);

    private readonly SearchFlightsValidator validator = new(new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 23, 30, 0, TimeSpan.Zero)), new FlightsOptions());

    private static SearchFlightsCommand Valid() =>
        new("IST", "LHR", Today.AddDays(21), null, 1, 0, 0, Cabin.Economy);

    private async Task<(string Target, string Code)[]> ProblemsAsync(SearchFlightsCommand command)
    {
        GuardResult result = await validator.ValidateAsync(command, CancellationToken.None);
        return [.. result.Errors.Select(static e => (e.ParameterName, e.ErrorCode!))];
    }

    [Fact]
    public async Task A_good_search_has_no_problems() =>
        (await ProblemsAsync(Valid())).ShouldBeEmpty();

    [Fact]
    public async Task A_return_trip_with_a_family_is_fine() =>
        (await ProblemsAsync(Valid() with { ReturnDate = Today.AddDays(28), Adults = 2, Children = 2, Infants = 1 })).ShouldBeEmpty();

    [Theory]
    [InlineData("ist")]
    [InlineData("IS")]
    [InlineData("")]
    [InlineData("LONDON")]
    public async Task An_airport_is_three_upper_case_letters(string code)
    {
        (await ProblemsAsync(Valid() with { Origin = code })).ShouldBe([("Origin", "InvalidAirport")]);
        (await ProblemsAsync(Valid() with { Destination = code })).ShouldContain(("Destination", "InvalidAirport"));
    }

    [Fact]
    public async Task Origin_and_destination_must_differ() =>
        (await ProblemsAsync(Valid() with { Destination = "IST" })).ShouldBe([("Destination", "OriginEqualsDestination")]);

    [Fact]
    public async Task Today_is_allowed_and_yesterday_is_not()
    {
        (await ProblemsAsync(Valid() with { DepartureDate = Today })).ShouldBeEmpty();
        (await ProblemsAsync(Valid() with { DepartureDate = Today.AddDays(-1) })).ShouldBe([("DepartureDate", "DepartureInPast")]);
    }

    [Fact]
    public async Task The_booking_horizon_is_330_days()
    {
        (await ProblemsAsync(Valid() with { DepartureDate = Today.AddDays(330) })).ShouldBeEmpty();
        (await ProblemsAsync(Valid() with { DepartureDate = Today.AddDays(331) })).ShouldBe([("DepartureDate", "DepartureTooFar")]);
    }

    [Fact]
    public async Task The_return_cannot_precede_the_departure_and_has_the_same_horizon()
    {
        (await ProblemsAsync(Valid() with { ReturnDate = Today.AddDays(20) })).ShouldBe([("ReturnDate", "ReturnBeforeDeparture")]);
        (await ProblemsAsync(Valid() with { DepartureDate = Today.AddDays(325), ReturnDate = Today.AddDays(340) })).ShouldBe([("ReturnDate", "DepartureTooFar")]);
        (await ProblemsAsync(Valid() with { ReturnDate = Valid().DepartureDate })).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, 1, 0, "adults", "AdultRequired")]
    [InlineData(1, 0, 2, "infants", "TooManyInfants")]
    [InlineData(6, 4, 0, "adults", "TooManyPassengers")]
    [InlineData(-1, 0, 0, "adults", "NegativePassengerCount")]
    public async Task The_passenger_mix_rules_report_their_domain_codes(int adults, int children, int infants, string target, string code) =>
        (await ProblemsAsync(Valid() with { Adults = adults, Children = children, Infants = infants }))
            .Select(static p => (p.Target.ToLowerInvariant(), p.Code)).ShouldContain((target, code));

    [Fact]
    public async Task Every_broken_field_is_reported_at_once()
    {
        (string Target, string Code)[] problems = await ProblemsAsync(
            new SearchFlightsCommand("xx", "LHR", Today.AddDays(-3), null, 0, 0, 0, (Cabin)7));

        problems.ShouldContain(("Origin", "InvalidAirport"));
        problems.ShouldContain(("DepartureDate", "DepartureInPast"));
        problems.ShouldContain(("Cabin", "InvalidCabin"));
        problems.ShouldContain(("Adults", "AdultRequired"));
    }
}
