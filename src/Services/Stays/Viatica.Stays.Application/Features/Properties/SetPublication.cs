using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Properties;

/// <summary>Puts a property on sale or takes it off. Publishing again, or unpublishing twice, changes nothing.</summary>
public sealed record PublishProperty(Guid PropertyId, Caller Caller, ulong? ExpectedVersion) : IRequest<Resource<PropertyView>>;

/// <summary>An admin may unpublish any listing; a partner only their own.</summary>
public sealed record UnpublishProperty(Guid PropertyId, Caller Caller, ulong? ExpectedVersion) : IRequest<Resource<PropertyView>>;

public sealed class PublishPropertyHandler(IStaysDb db, TimeProvider clock) : IRequestHandler<PublishProperty, Resource<PropertyView>>
{
    public async Task<Resource<PropertyView>> Handle(PublishProperty request, CancellationToken cancellationToken)
    {
        StayProperty property = await OwnedProperty.LoadAsync(db, request.PropertyId, request.Caller, cancellationToken);
        OwnedProperty.CheckVersion(property.Version, request.ExpectedVersion);

        if (property.Publish(clock.GetUtcNow()))
        {
            db.Publish(ListingEvents.Snapshot(property));
            await db.SaveChangesAsync(cancellationToken);
        }

        return new Resource<PropertyView>(PropertyView.From(property), property.Version);
    }
}

public sealed class UnpublishPropertyHandler(IStaysDb db, TimeProvider clock) : IRequestHandler<UnpublishProperty, Resource<PropertyView>>
{
    public async Task<Resource<PropertyView>> Handle(UnpublishProperty request, CancellationToken cancellationToken)
    {
        StayProperty property = await db.Properties.Include(p => p.RoomTypes)
            .FirstOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken)
            ?? throw StayErrors.PropertyNotFound();

        if (!request.Caller.IsAdmin && !request.Caller.Owns(property))
        {
            throw StayErrors.PropertyNotFound();
        }

        OwnedProperty.CheckVersion(property.Version, request.ExpectedVersion);

        if (property.Unpublish(clock.GetUtcNow()))
        {
            db.Publish(ListingEvents.Unpublished(property));
            await db.SaveChangesAsync(cancellationToken);
        }

        return new Resource<PropertyView>(PropertyView.From(property), property.Version);
    }
}
