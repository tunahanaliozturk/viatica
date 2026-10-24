using Viatica.Kernel.Pii;

namespace Viatica.Flights.Domain;

/// <summary>One thing wrong with the passenger list. It names the traveller by position and the field, never by value.</summary>
public readonly record struct PassengerProblem(int Index, string Field, string Code);

/// <summary>
/// What FLT-5 asks of passenger details before an order is created: names the airline will accept, dates of birth
/// that fit the passenger type on the day of travel, and a list that adds up to the offer's passenger mix.
/// </summary>
/// <remarks>
/// The age bands are the airlines' own (adult from 12, child 2 to 11, infant under 2), checked on the date of the
/// first flight, because that is the date the airline checks. Nothing here returns or logs a value from the list:
/// a problem is a position, a field name and a code.
/// </remarks>
public static class PassengerRules
{
    public const int MaxNameLength = 60;
    private const int OldestPlausibleAge = 120;

    public static IReadOnlyList<PassengerProblem> Validate(
        IReadOnlyList<PassengerDetails> passengers, PassengerMix expected, DateOnly travelDate)
    {
        ArgumentNullException.ThrowIfNull(passengers);
        ArgumentNullException.ThrowIfNull(expected);

        var problems = new List<PassengerProblem>();

        if (passengers.Count != expected.Total
            || passengers.Count(static p => p.Type == PassengerType.Adult) != expected.Adults
            || passengers.Count(static p => p.Type == PassengerType.Child) != expected.Children
            || passengers.Count(static p => p.Type == PassengerType.Infant) != expected.Infants)
        {
            problems.Add(new PassengerProblem(-1, "passengers", "PassengerMixMismatch"));
        }

        for (int i = 0; i < passengers.Count; i++)
        {
            PassengerDetails passenger = passengers[i];

            if (!IsName(passenger.GivenName))
            {
                problems.Add(new PassengerProblem(i, "givenName", "InvalidName"));
            }

            if (!IsName(passenger.Surname))
            {
                problems.Add(new PassengerProblem(i, "surname", "InvalidName"));
            }

            if (!BirthDateFits(passenger.Type, passenger.BirthDate, travelDate))
            {
                problems.Add(new PassengerProblem(i, "birthDate", "BirthDateDoesNotFitType"));
            }

            if (passenger.DocumentNumber is { } document && !IsDocumentNumber(document))
            {
                problems.Add(new PassengerProblem(i, "documentNumber", "InvalidDocumentNumber"));
            }
        }

        return problems;
    }

    public static int AgeOn(DateOnly birthDate, DateOnly day)
    {
        int age = day.Year - birthDate.Year;
        return birthDate > day.AddYears(-age) ? age - 1 : age;
    }

    private static bool BirthDateFits(PassengerType type, DateOnly birthDate, DateOnly travelDate)
    {
        if (birthDate > travelDate)
        {
            return false;
        }

        int age = AgeOn(birthDate, travelDate);
        return age <= OldestPlausibleAge && type switch
        {
            PassengerType.Adult => age >= 12,
            PassengerType.Child => age is >= 2 and < 12,
            PassengerType.Infant => age < 2,
            _ => false,
        };
    }

    // Letters, spaces, hyphens and apostrophes: what a passport name line holds and what the airlines accept.
    private static bool IsName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength
        && name.All(static c => char.IsLetter(c) || c is ' ' or '-' or '\'');

    private static bool IsDocumentNumber(string document) =>
        document.Length is >= 5 and <= 20 && document.All(char.IsAsciiLetterOrDigit);
}
