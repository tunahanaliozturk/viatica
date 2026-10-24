using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Cars.Domain;

namespace Viatica.Cars.Application.Features.PublishLocation;

/// <summary>Puts a location in front of travellers, or takes it off. Both are safe to repeat.</summary>
public sealed record PublishLocation(Guid PartnerId, Guid Id, bool Publish) : IRequest<Versioned<LocationView>>;

public sealed class PublishLocationHandler(ICarsDb db, TimeProvider clock) : IRequestHandler<PublishLocation, Versioned<LocationView>>
{
    public async Task<Versioned<LocationView>> Handle(PublishLocation request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        RentalLocation location = await db.Locations
            .FirstOrDefaultAsync(l => l.Id == request.Id && l.PartnerId == request.PartnerId, cancellationToken)
            ?? throw CarErrors.LocationNotFound();

        return await db.InTransactionAsync(
            async transaction =>
            {
                if (request.Publish && location.Publish(now))
                {
                    await Announcements.ListingAsync(db, location, now, cancellationToken);
                }
                else if (!request.Publish && location.Unpublish(now))
                {
                    Announcements.Withdrawn(db, location);
                }

                await db.SaveChangesAsync(cancellationToken);
                return new Versioned<LocationView>(LocationView.From(location), location.Version);
            },
            cancellationToken);
    }
}
