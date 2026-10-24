namespace Viatica.Flights.Domain;

/// <summary>
/// How many of each kind of traveller a search or an order is for. An infant sits on a lap and takes no seat, which
/// is why the mix, not a headcount, is what the airline prices.
/// </summary>
/// <remarks>
/// The limits are the usual airline ones: at least one adult, no more infants than adults (each infant needs a lap),
/// and nine travellers on one booking. The nine counts everyone, infants included. That is stricter than airlines
/// that count seats only, and it is the safer reading when a supplier's own limit is not known.
/// </remarks>
public sealed record PassengerMix
{
    public const int MaxTravellers = 9;

    public PassengerMix(int adults, int children, int infants)
    {
        if (Check(adults, children, infants) is { } problem)
        {
            throw new ArgumentException(problem.Message);
        }

        Adults = adults;
        Children = children;
        Infants = infants;
    }

    public int Adults { get; }

    public int Children { get; }

    public int Infants { get; }

    public int Total => Adults + Children + Infants;

    /// <summary>Seats taken: infants travel on a lap.</summary>
    public int Seats => Adults + Children;

    /// <summary>The first rule the mix breaks, or null when it is a valid mix. The code is part of the API contract.</summary>
    public static MixProblem? Check(int adults, int children, int infants)
    {
        if (adults < 0 || children < 0 || infants < 0)
        {
            return MixProblems.Negative;
        }

        if (adults < 1)
        {
            return MixProblems.AdultRequired;
        }

        if (infants > adults)
        {
            return MixProblems.TooManyInfants;
        }

        return adults + children + infants > MaxTravellers ? MixProblems.TooManyPassengers : null;
    }
}

/// <summary>The rules a passenger mix can break. A validator turns each into one error with its own code.</summary>
public static class MixProblems
{
    public static readonly MixProblem Negative =
        new("NegativePassengerCount", "Passenger counts cannot be negative.", "Adults");

    public static readonly MixProblem AdultRequired =
        new("AdultRequired", "At least one adult must travel.", "Adults");

    public static readonly MixProblem TooManyInfants =
        new("TooManyInfants", "Each infant needs an adult to sit with, so infants cannot outnumber adults.", "Infants");

    public static readonly MixProblem TooManyPassengers =
        new("TooManyPassengers", "At most 9 travellers can be on one booking.", "Adults");

    public static IReadOnlyList<MixProblem> All { get; } = [Negative, AdultRequired, TooManyInfants, TooManyPassengers];
}

/// <summary>A broken rule: a stable code, a message for people, and the request field it is about.</summary>
public readonly record struct MixProblem(string Code, string Message, string Target);
