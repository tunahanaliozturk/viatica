using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Properties;

/// <summary>Creates a draft property with an id the client chose, so sending the same request twice is harmless.</summary>
public sealed record CreateProperty(Guid PropertyId, Guid PartnerId, PropertyDetails Details) : IRequest<Resource<PropertyView>>;

public sealed class CreatePropertyValidator : PropertyRulesValidator<CreateProperty>
{
    public CreatePropertyValidator()
        : base(static c => PropertyPatch.From(c.Details))
    {
        RuleFor(static c => c.PropertyId != Guid.Empty, "the property id must not be empty.", "PropertyId");
    }
}

public sealed class CreatePropertyHandler(IStaysDb db, TimeProvider clock) : IRequestHandler<CreateProperty, Resource<PropertyView>>
{
    public async Task<Resource<PropertyView>> Handle(CreateProperty request, CancellationToken cancellationToken)
    {
        StayProperty? existing = await db.Properties.AsNoTracking().Include(p => p.RoomTypes)
            .FirstOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken);

        if (existing is not null)
        {
            // Another partner's id answers as not found, like every other read of their property. The owner's own
            // retry gets the property back; anything else is an update in disguise and says so.
            if (existing.PartnerId != request.PartnerId)
            {
                throw StayErrors.PropertyNotFound();
            }

            return existing.Details.Equals(request.Details)
                ? new Resource<PropertyView>(PropertyView.From(existing), existing.Version)
                : throw StayErrors.AlreadyExists("property");
        }

        var property = StayProperty.Create(request.PropertyId, request.PartnerId, request.Details, clock.GetUtcNow());
        db.Properties.Add(property);
        await db.SaveChangesAsync(cancellationToken);

        return new Resource<PropertyView>(PropertyView.From(property), property.Version, IsNew: true);
    }
}
