using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Properties;

/// <summary>
/// One property with its room types. Anyone can read a published property; a draft or an unpublished one is
/// visible to its partner and to an admin, and answers 404 to everybody else.
/// </summary>
public sealed record GetProperty(Guid PropertyId, Caller Caller) : IRequest<Resource<PropertyView>>;

public sealed class GetPropertyHandler(IStaysDb db) : IRequestHandler<GetProperty, Resource<PropertyView>>
{
    public async Task<Resource<PropertyView>> Handle(GetProperty request, CancellationToken cancellationToken)
    {
        StayProperty property = await db.Properties.AsNoTracking().Include(p => p.RoomTypes)
            .FirstOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken)
            ?? throw StayErrors.PropertyNotFound();

        return request.Caller.CanSee(property)
            ? new Resource<PropertyView>(PropertyView.From(property), property.Version)
            : throw StayErrors.PropertyNotFound();
    }
}
