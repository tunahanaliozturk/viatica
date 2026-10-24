using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Kernel;
using Viatica.Stays.Application.Features.Properties;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.RoomTypes;

/// <summary>Whatever is present replaces what is stored. A rate is in euro, as every price is in v1.</summary>
public sealed record RoomTypePatch(
    string? Name = null,
    int? MaxOccupancy = null,
    string? Beds = null,
    IReadOnlyList<string>? Amenities = null,
    Money? BaseRate = null)
{
    public static RoomTypePatch From(RoomTypeDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return new RoomTypePatch(details.Name, details.MaxOccupancy, details.Beds, details.Amenities, details.BaseRate);
    }

    public RoomTypeDetails ApplyTo(RoomTypeDetails current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return new RoomTypeDetails(
            Name ?? current.Name,
            MaxOccupancy ?? current.MaxOccupancy,
            Beds ?? current.Beds,
            Amenities ?? current.Amenities,
            BaseRate ?? current.BaseRate);
    }
}

public abstract class RoomTypeRulesValidator<TRequest> : AbstractValidator<TRequest>
{
    protected RoomTypeRulesValidator(Func<TRequest, RoomTypePatch> patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        RuleFor(r => patch(r).Name is not { } v || (!string.IsNullOrWhiteSpace(v) && v.Length <= 100), "name must be 1 to 100 characters.", "Name");
        RuleFor(r => patch(r).MaxOccupancy is not { } v || v is >= 1 and <= 20, "maxOccupancy must be between 1 and 20.", "MaxOccupancy");
        RuleFor(r => patch(r).Beds is not { } v || (!string.IsNullOrWhiteSpace(v) && v.Length <= 100), "beds must be 1 to 100 characters.", "Beds");
        RuleFor(
            r => patch(r).Amenities is not { } a || (a.Count <= 50 && a.All(static x => !string.IsNullOrWhiteSpace(x) && x.Length <= 50)),
            "amenities holds at most 50 entries of 1 to 50 characters.",
            "Amenities");
        RuleFor(
            r => patch(r).BaseRate is not { } m || (m.Currency == Money.Euro && m.Amount is > 0 and <= 100_000),
            "baseRate must be a positive amount in EUR.",
            "BaseRate");
    }
}

/// <summary>Adds a room type to a property with an id the client chose.</summary>
public sealed record PutRoomType(Guid PropertyId, Guid RoomTypeId, Caller Caller, RoomTypeDetails Details)
    : IRequest<Resource<RoomTypeView>>;

public sealed class PutRoomTypeValidator : RoomTypeRulesValidator<PutRoomType>
{
    public PutRoomTypeValidator()
        : base(static c => RoomTypePatch.From(c.Details))
    {
    }
}

public sealed class PutRoomTypeHandler(IStaysDb db, TimeProvider clock) : IRequestHandler<PutRoomType, Resource<RoomTypeView>>
{
    public async Task<Resource<RoomTypeView>> Handle(PutRoomType request, CancellationToken cancellationToken)
    {
        StayProperty property = await OwnedProperty.LoadAsync(db, request.PropertyId, request.Caller, cancellationToken);

        if (property.RoomTypes.FirstOrDefault(r => r.Id == request.RoomTypeId) is { } existing)
        {
            return existing.Details.Equals(request.Details)
                ? new Resource<RoomTypeView>(RoomTypeView.From(existing), existing.Version)
                : throw StayErrors.AlreadyExists("room type");
        }

        DateTimeOffset now = clock.GetUtcNow();
        var roomType = RoomType.Create(request.RoomTypeId, property.Id, request.Details, now);
        property.RoomTypes.Add(roomType);
        db.Inventories.Add(RoomTypeInventory.For(roomType.Id));

        // The listing now has another room type, so it is a new version of itself and, when on sale, a new snapshot.
        property.RoomTypesChanged(now);
        ListingEvents.PublishIfListed(db, property);

        await db.SaveChangesAsync(cancellationToken);
        return new Resource<RoomTypeView>(RoomTypeView.From(roomType), roomType.Version, IsNew: true);
    }
}

/// <summary>Changes a room type; <see cref="ExpectedVersion"/> is the room type's own ETag.</summary>
public sealed record UpdateRoomType(Guid PropertyId, Guid RoomTypeId, Caller Caller, RoomTypePatch Patch, ulong? ExpectedVersion)
    : IRequest<Resource<RoomTypeView>>;

public sealed class UpdateRoomTypeValidator : RoomTypeRulesValidator<UpdateRoomType>
{
    public UpdateRoomTypeValidator()
        : base(static u => u.Patch)
    {
    }
}

public sealed class UpdateRoomTypeHandler(IStaysDb db, TimeProvider clock) : IRequestHandler<UpdateRoomType, Resource<RoomTypeView>>
{
    public async Task<Resource<RoomTypeView>> Handle(UpdateRoomType request, CancellationToken cancellationToken)
    {
        StayProperty property = await OwnedProperty.LoadAsync(db, request.PropertyId, request.Caller, cancellationToken);
        RoomType roomType = property.RoomTypes.FirstOrDefault(r => r.Id == request.RoomTypeId)
            ?? throw StayErrors.RoomTypeNotFound();
        OwnedProperty.CheckVersion(roomType.Version, request.ExpectedVersion);

        DateTimeOffset now = clock.GetUtcNow();
        roomType.Revise(request.Patch.ApplyTo(roomType.Details), now);
        property.RoomTypesChanged(now);
        ListingEvents.PublishIfListed(db, property);

        await db.SaveChangesAsync(cancellationToken);
        return new Resource<RoomTypeView>(RoomTypeView.From(roomType), roomType.Version);
    }
}

public sealed record GetRoomType(Guid PropertyId, Guid RoomTypeId, Caller Caller) : IRequest<Resource<RoomTypeView>>;

public sealed class GetRoomTypeHandler(IStaysDb db) : IRequestHandler<GetRoomType, Resource<RoomTypeView>>
{
    public async Task<Resource<RoomTypeView>> Handle(GetRoomType request, CancellationToken cancellationToken)
    {
        StayProperty property = await db.Properties.AsNoTracking().Include(p => p.RoomTypes)
            .FirstOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken)
            ?? throw StayErrors.PropertyNotFound();

        if (!request.Caller.CanSee(property))
        {
            throw StayErrors.PropertyNotFound();
        }

        RoomType roomType = property.RoomTypes.FirstOrDefault(r => r.Id == request.RoomTypeId)
            ?? throw StayErrors.RoomTypeNotFound();
        return new Resource<RoomTypeView>(RoomTypeView.From(roomType), roomType.Version);
    }
}
