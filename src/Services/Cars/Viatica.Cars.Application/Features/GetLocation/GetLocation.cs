using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Cars.Domain;

namespace Viatica.Cars.Application.Features.GetLocation;

public sealed record GetLocation(Guid PartnerId, Guid Id) : IRequest<Versioned<LocationView>>;

public sealed class GetLocationHandler(ICarsDb db) : IRequestHandler<GetLocation, Versioned<LocationView>>
{
    public async Task<Versioned<LocationView>> Handle(GetLocation request, CancellationToken cancellationToken)
    {
        RentalLocation location = await db.Locations.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == request.Id && l.PartnerId == request.PartnerId, cancellationToken)
            ?? throw CarErrors.LocationNotFound();

        return new Versioned<LocationView>(LocationView.From(location), location.Version);
    }
}
