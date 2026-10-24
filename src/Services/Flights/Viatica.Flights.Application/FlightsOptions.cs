namespace Viatica.Flights.Application;

/// <summary>Behaviour settings for the use cases, bound from the <c>Flights</c> configuration section.</summary>
public sealed class FlightsOptions
{
    public const string Section = "Flights";

    /// <summary>
    /// How long a search waits for the airlines in all. An airline still working when it passes is left out of the
    /// response and named in <c>incompleteSuppliers</c>. 2.5 s is the figure in FLT-1.
    /// </summary>
    public TimeSpan SearchDeadline { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>How long a search with no offers at all stays cached; with offers, the earliest offer decides.</summary>
    public TimeSpan EmptySearchLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Furthest ahead a flight can be searched. The airlines sell about 330 days out.</summary>
    public int BookingHorizonDays { get; set; } = 330;
}
