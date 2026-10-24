namespace Viatica.Flights.Application;

/// <summary>The codes this service adds to the shared vocabulary. They are listed in docs/api/errors.md.</summary>
public static class FlightErrorCodes
{
    public const string SearchExpired = nameof(SearchExpired);
    public const string OfferExpired = nameof(OfferExpired);
    public const string PriceChanged = nameof(PriceChanged);
    public const string InvalidPassengers = nameof(InvalidPassengers);
    public const string NoSuppliersAvailable = nameof(NoSuppliersAvailable);
    public const string SupplierUnavailable = nameof(SupplierUnavailable);
}
