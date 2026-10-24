using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Properties;

/// <summary>
/// Changes a property. <see cref="ExpectedVersion"/> is the ETag the client read, or null for <c>If-Match: *</c>.
/// </summary>
public sealed record UpdateProperty(Guid PropertyId, Caller Caller, PropertyPatch Patch, ulong? ExpectedVersion)
    : IRequest<Resource<PropertyView>>;

public sealed class UpdatePropertyValidator : PropertyRulesValidator<UpdateProperty>
{
    public UpdatePropertyValidator()
        : base(static u => u.Patch)
    {
    }
}

public sealed class UpdatePropertyHandler(IStaysDb db, TimeProvider clock) : IRequestHandler<UpdateProperty, Resource<PropertyView>>
{
    public async Task<Resource<PropertyView>> Handle(UpdateProperty request, CancellationToken cancellationToken)
    {
        StayProperty property = await OwnedProperty.LoadAsync(db, request.PropertyId, request.Caller, cancellationToken);
        OwnedProperty.CheckVersion(property.Version, request.ExpectedVersion);

        property.Revise(request.Patch.ApplyTo(property.Details), clock.GetUtcNow());
        ListingEvents.PublishIfListed(db, property);

        // The concurrency token on the row turns a writer that slipped in after the version check into a 412.
        await db.SaveChangesAsync(cancellationToken);
        return new Resource<PropertyView>(PropertyView.From(property), property.Version);
    }
}

/// <summary>The two steps every change to a partner's own property starts with.</summary>
internal static class OwnedProperty
{
    /// <summary>
    /// Loads the property with its room types, tracked. Someone else's property answers exactly like a missing
    /// one, so an id never tells a partner that a rival holds it.
    /// </summary>
    public static async Task<StayProperty> LoadAsync(IStaysDb db, Guid propertyId, Caller caller, CancellationToken cancellationToken)
    {
        StayProperty? property = await db.Properties.Include(p => p.RoomTypes)
            .FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);

        return property is not null && caller.Owns(property) ? property : throw StayErrors.PropertyNotFound();
    }

    public static void CheckVersion(uint current, ulong? expected)
    {
        if (expected is { } version && version != current)
        {
            throw Viatica.Kernel.ApiErrorException.PreconditionFailed();
        }
    }
}
