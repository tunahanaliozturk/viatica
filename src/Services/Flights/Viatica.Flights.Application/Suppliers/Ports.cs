namespace Viatica.Flights.Application.Suppliers;

/// <summary>Looking and pricing: the half of the airline API that changes nothing at the airline.</summary>
public interface IAirlineShopping
{
    /// <summary>The airlines a search fans out to, from configuration.</summary>
    IReadOnlyList<string> Airlines { get; }

    /// <exception cref="OperationCanceledException">The caller's token fired. Timeouts of the airline's own are a <see cref="SupplierFailure"/>.</exception>
    Task<SearchOutcome> SearchAsync(string airline, SearchCriteria criteria, CancellationToken cancellationToken);

    Task<RepriceOutcome> RepriceAsync(string airline, string supplierOfferId, CancellationToken cancellationToken);
}

/// <summary>Orders and their lifecycle: everything that changes state at the airline.</summary>
public interface IAirlineOrdering
{
    /// <summary>Never retried blindly: a timed out creation is looked up with <see cref="FindOrderAsync"/>.</summary>
    Task<CreateOrderOutcome> CreateOrderAsync(string airline, NewSupplierOrder order, CancellationToken cancellationToken);

    Task<FindOrderOutcome> FindOrderAsync(string airline, string idempotencyKey, CancellationToken cancellationToken);

    Task<TicketingOutcome> TicketAsync(string airline, string bookingReference, CancellationToken cancellationToken);

    Task<VoidOutcome> VoidAsync(string airline, string bookingReference, CancellationToken cancellationToken);

    Task<CancelOutcome> CancelAsync(string airline, string bookingReference, CancellationToken cancellationToken);
}
