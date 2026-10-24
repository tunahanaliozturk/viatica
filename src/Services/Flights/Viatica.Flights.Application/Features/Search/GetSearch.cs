using Relevox;
using Viatica.Flights.Application.Offers;
using Viatica.Kernel;
using Viatica.Kernel.Querying;

namespace Viatica.Flights.Application.Features.Search;

/// <summary>One page of a search already made. Reads the cache and never calls an airline (FLT-2).</summary>
public sealed record GetSearchQuery(Guid SearchId, CollectionQuery<OfferRow> Query) : IRequest<FlightSearchPage>;

public sealed class GetSearchHandler(IOfferStore store) : IRequestHandler<GetSearchQuery, FlightSearchPage>
{
    public async Task<FlightSearchPage> Handle(GetSearchQuery request, CancellationToken cancellationToken)
    {
        StoredSearch search = await store.GetSearchAsync(request.SearchId, cancellationToken)
            ?? throw ApiErrorException.Domain(
                ErrorCategory.NotFound,
                FlightErrorCodes.SearchExpired,
                "This search has expired or never existed. Search again.");

        return OfferPaging.Page(search, request.Query);
    }
}
