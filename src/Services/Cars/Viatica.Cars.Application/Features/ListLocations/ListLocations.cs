using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Cars.Domain;
using Viatica.Kernel.Querying;

namespace Viatica.Cars.Application.Features.ListLocations;

/// <summary>A page of the partner's own locations.</summary>
public sealed record ListLocations(Guid PartnerId, CollectionQuery<RentalLocation> Query) : IRequest<KeysetPage<LocationView>>
{
    /// <summary>What a caller may filter and sort on. The partner is not a field: it always comes from the token.</summary>
    public static QueryFields<RentalLocation> Fields { get; } = new QueryFields<RentalLocation>()
        .Key("id", static l => l.Id)
        .Field("name", static l => l.Name, sortable: true)
        .Field("status", static l => l.Status)
        .Field("airportCode", static l => l.AirportCode)
        .Field("city", static l => l.Address.City)
        .DefaultOrderBy("name");
}

public sealed class ListLocationsHandler(ICarsDb db) : IRequestHandler<ListLocations, KeysetPage<LocationView>>
{
    public async Task<KeysetPage<LocationView>> Handle(ListLocations request, CancellationToken cancellationToken)
    {
        List<RentalLocation> rows = await request.Query
            .ApplyTo(db.Locations.AsNoTracking().Where(l => l.PartnerId == request.PartnerId))
            .ToListAsync(cancellationToken);

        return request.Query.ToPage(rows, LocationView.From);
    }
}
