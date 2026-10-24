using Viatica.Flights.Domain;
using Viatica.Kernel;
using Viatica.Kernel.Pii;

namespace Viatica.Flights.UnitTests;

public sealed class PassengerMixTests
{
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 2, 2)]
    [InlineData(9, 0, 0)]
    [InlineData(4, 2, 3)]
    public void Valid_mixes_are_accepted(int adults, int children, int infants)
    {
        PassengerMix.Check(adults, children, infants).ShouldBeNull();
        new PassengerMix(adults, children, infants).Total.ShouldBe(adults + children + infants);
    }

    [Theory]
    [InlineData(0, 1, 0, "AdultRequired")]
    [InlineData(0, 0, 0, "AdultRequired")]
    [InlineData(1, 0, 2, "TooManyInfants")]
    [InlineData(2, 1, 3, "TooManyInfants")]
    [InlineData(5, 4, 1, "TooManyPassengers")]
    [InlineData(10, 0, 0, "TooManyPassengers")]
    [InlineData(-1, 0, 0, "NegativePassengerCount")]
    public void Each_broken_rule_has_its_own_stable_code(int adults, int children, int infants, string code)
    {
        PassengerMix.Check(adults, children, infants)!.Value.Code.ShouldBe(code);
        Should.Throw<ArgumentException>(() => new PassengerMix(adults, children, infants));
    }

    [Fact]
    public void Infants_travel_on_a_lap_and_take_no_seat() =>
        new PassengerMix(2, 1, 2).Seats.ShouldBe(3);

    [Fact]
    public void Nine_travellers_means_nine_in_all_infants_included() =>
        PassengerMix.Check(5, 3, 1).ShouldBeNull();
}

public sealed class PassengerRulesTests
{
    private static readonly DateOnly Travel = new(2026, 11, 23);

    private static PassengerDetails Adult(string given = "Ayse", string surname = "Yilmaz", string birth = "1988-04-02", string? document = "U12345678") =>
        new(PassengerType.Adult, given, surname, DateOnly.Parse(birth), document);

    private static PassengerDetails Child(string birth) => new(PassengerType.Child, "Deniz", "Yilmaz", DateOnly.Parse(birth));

    private static PassengerDetails Infant(string birth) => new(PassengerType.Infant, "Ela", "Yilmaz", DateOnly.Parse(birth));

    [Fact]
    public void A_list_that_fits_the_mix_has_no_problems() =>
        PassengerRules.Validate([Adult(), Child("2018-01-01"), Infant("2026-03-01")], new PassengerMix(1, 1, 1), Travel).ShouldBeEmpty();

    [Theory]
    [InlineData("2014-11-23", true)]   // turned 12 on the day of travel: an adult
    [InlineData("2014-11-24", false)]  // turns 12 the day after
    [InlineData("1906-11-23", true)]   // 120
    [InlineData("1905-01-01", false)]  // implausible
    [InlineData("2026-11-24", false)]  // born after the flight
    public void An_adult_is_twelve_or_over_on_the_day_of_travel(string birth, bool accepted) =>
        PassengerRules.Validate([Adult(birth: birth)], new PassengerMix(1, 0, 0), Travel).IsEmpty().ShouldBe(accepted);

    [Theory]
    [InlineData("2024-11-23", true)]   // just turned 2
    [InlineData("2024-11-24", false)]  // still an infant
    [InlineData("2014-11-24", true)]   // 11 until the day after
    [InlineData("2014-11-23", false)]  // 12: an adult fare
    public void A_child_is_two_to_eleven(string birth, bool accepted) =>
        PassengerRules.Validate([Adult(), Child(birth)], new PassengerMix(1, 1, 0), Travel).IsEmpty().ShouldBe(accepted);

    [Theory]
    [InlineData("2026-11-23", true)]
    [InlineData("2024-11-24", true)]   // one day short of two
    [InlineData("2024-11-23", false)]  // two: needs a seat
    public void An_infant_is_under_two(string birth, bool accepted) =>
        PassengerRules.Validate([Adult(), Infant(birth)], new PassengerMix(1, 0, 1), Travel).IsEmpty().ShouldBe(accepted);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ayse1")]
    [InlineData("Ayse<script>")]
    [InlineData("A very long given name that goes on and on and on and on and on and on and on")]
    public void A_name_that_a_passport_could_not_hold_is_refused(string name)
    {
        IReadOnlyList<PassengerProblem> problems = PassengerRules.Validate([Adult(given: name)], new PassengerMix(1, 0, 0), Travel);

        problems.ShouldBe([new PassengerProblem(0, "givenName", "InvalidName")]);
    }

    [Theory]
    [InlineData("Ayse")]
    [InlineData("Jean-Luc")]
    [InlineData("O'Brien")]
    [InlineData("Ayşe")]
    [InlineData("Maria del Carmen")]
    public void Letters_spaces_hyphens_and_apostrophes_are_names(string name) =>
        PassengerRules.Validate([Adult(surname: name)], new PassengerMix(1, 0, 0), Travel).ShouldBeEmpty();

    [Theory]
    [InlineData("U1", false)]
    [InlineData("U12345678", true)]
    [InlineData("U123 45678", false)]
    [InlineData("123456789012345678901", false)]
    public void A_document_number_is_five_to_twenty_letters_or_digits(string document, bool accepted) =>
        PassengerRules.Validate([Adult(document: document)], new PassengerMix(1, 0, 0), Travel).IsEmpty().ShouldBe(accepted);

    [Fact]
    public void The_document_is_optional() =>
        PassengerRules.Validate([Adult(document: null)], new PassengerMix(1, 0, 0), Travel).ShouldBeEmpty();

    [Fact]
    public void A_list_that_does_not_add_up_to_the_offer_is_one_problem_about_the_list()
    {
        PassengerRules.Validate([Adult()], new PassengerMix(2, 0, 0), Travel)
            .ShouldBe([new PassengerProblem(-1, "passengers", "PassengerMixMismatch")]);
        PassengerRules.Validate([Adult(), Adult()], new PassengerMix(1, 1, 0), Travel)
            .ShouldContain(new PassengerProblem(-1, "passengers", "PassengerMixMismatch"));
    }

    [Fact]
    public void A_problem_names_a_position_and_a_field_and_never_a_value()
    {
        IReadOnlyList<PassengerProblem> problems = PassengerRules.Validate(
            [Adult(), Adult(given: "Zeynep", birth: "2026-01-01", document: "x")], new PassengerMix(2, 0, 0), Travel);

        string printed = string.Join(" ", problems.Select(static p => p.ToString()));
        printed.ShouldNotContain("Zeynep");
        printed.ShouldNotContain("2026-01-01");
        problems.Select(static p => p.Index).ShouldAllBe(static i => i == 1);
    }

    [Theory]
    [InlineData("2000-02-29", "2026-02-28", 25)]
    [InlineData("2000-02-29", "2026-03-01", 26)]
    [InlineData("2014-11-24", "2026-11-23", 11)]
    public void Age_counts_whole_years_on_the_day(string birth, string day, int age) =>
        PassengerRules.AgeOn(DateOnly.Parse(birth), DateOnly.Parse(day)).ShouldBe(age);
}

internal static class ProblemExtensions
{
    public static bool IsEmpty(this IReadOnlyList<PassengerProblem> problems) => problems.Count == 0;
}

public sealed class FlightOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    private static FlightOrder Booked() => FlightOrder.Create(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "ZC", "PPT4VZ", "offer", Money.Eur(258.88m), T0.AddDays(1), "sealed", T0);

    [Fact]
    public void A_new_order_is_booked_and_keyed_by_its_item()
    {
        FlightOrder order = Booked();

        order.Status.ShouldBe(FlightOrderStatus.Booked);
        order.IdempotencyKey.ShouldBe(order.ItemId.ToString("D"));
        order.Total.ShouldBe(Money.Eur(258.88m));
        order.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Ticketing_a_booked_order_records_the_tickets_and_a_repeat_changes_nothing()
    {
        FlightOrder order = Booked();

        order.Ticket(["9011234567890"], T0.AddMinutes(5)).ShouldBe(Transition.Applied);
        order.Ticket(["something else"], T0.AddMinutes(6)).ShouldBe(Transition.AlreadyApplied);

        order.Status.ShouldBe(FlightOrderStatus.Ticketed);
        order.TicketNumbers.ShouldBe(["9011234567890"]);
        order.UpdatedAt.ShouldBe(T0.AddMinutes(5));
    }

    [Fact]
    public void A_ticketing_refusal_is_remembered_and_cleared_by_a_later_success()
    {
        FlightOrder order = Booked();

        order.RecordTicketingRefusal("TICKETING_REFUSED: no", T0.AddMinutes(1));
        order.TicketingRefusal.ShouldBe("TICKETING_REFUSED: no");
        order.Status.ShouldBe(FlightOrderStatus.Booked);

        order.Ticket(["1"], T0.AddMinutes(2));
        order.TicketingRefusal.ShouldBeNull();
    }

    [Fact]
    public void A_booked_or_ticketed_order_can_be_voided_once()
    {
        FlightOrder booked = Booked();
        booked.Void(T0).ShouldBe(Transition.Applied);
        booked.Void(T0).ShouldBe(Transition.AlreadyApplied);

        FlightOrder ticketed = Booked();
        ticketed.Ticket(["1"], T0);
        ticketed.Void(T0).ShouldBe(Transition.Applied);
        ticketed.Status.ShouldBe(FlightOrderStatus.Voided);
    }

    [Fact]
    public void Only_a_ticketed_order_can_be_cancelled_and_it_keeps_the_refund()
    {
        FlightOrder order = Booked();
        order.Cancel(Money.Eur(10m), T0).ShouldBe(Transition.NotAllowed);

        order.Ticket(["1"], T0);
        order.Cancel(Money.Eur(42.88m), T0.AddHours(1)).ShouldBe(Transition.Applied);
        order.Cancel(Money.Eur(0m), T0.AddHours(2)).ShouldBe(Transition.AlreadyApplied);

        order.Status.ShouldBe(FlightOrderStatus.Cancelled);
        order.Refund.ShouldBe(Money.Eur(42.88m));
    }

    [Fact]
    public void A_closed_order_cannot_be_ticketed_or_closed_the_other_way()
    {
        FlightOrder voided = Booked();
        voided.Void(T0);
        voided.Ticket(["1"], T0).ShouldBe(Transition.NotAllowed);
        voided.Cancel(Money.Eur(0m), T0).ShouldBe(Transition.NotAllowed);

        FlightOrder cancelled = Booked();
        cancelled.Ticket(["1"], T0);
        cancelled.Cancel(Money.Eur(0m), T0);
        cancelled.Void(T0).ShouldBe(Transition.NotAllowed);
    }

    [Fact]
    public void A_refusal_after_the_order_closed_is_ignored()
    {
        FlightOrder order = Booked();
        order.Void(T0);

        order.RecordTicketingRefusal("late", T0);

        order.TicketingRefusal.ShouldBeNull();
    }
}

public sealed class IataCodeTests
{
    [Theory]
    [InlineData("IST", true)]
    [InlineData("ist", false)]
    [InlineData("IS", false)]
    [InlineData("ISTA", false)]
    [InlineData("I5T", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void An_airport_code_is_three_upper_case_letters(string? code, bool valid)
    {
        IataCode.IsValid(code).ShouldBe(valid);
        if (valid)
        {
            new IataCode(code!).Value.ShouldBe(code);
        }
    }
}
