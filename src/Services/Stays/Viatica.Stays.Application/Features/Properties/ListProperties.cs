using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Kernel.Querying;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Properties;

/// <summary>
/// A page of properties. The public list holds published properties only; the partner's list holds every
/// property of the calling partner, drafts included.
/// </summary>
public sealed record ListProperties(CollectionQuery<PropertyRow> Query, Caller Caller, bool OwnOnly) : IRequest<KeysetPage<PropertySummary>>;

public static class PropertyQueries
{
    /// <summary>
    /// What a caller may filter and sort the list on. <c>partnerId</c> is deliberately not here: the public list
    /// would let anyone enumerate one partner's properties.
    /// </summary>
    public static QueryFields<PropertyRow> Fields { get; } = new QueryFields<PropertyRow>()
        .Key("id", static p => p.Id)
        .Field("name", static p => p.Name, sortable: true)
        .Field("city", static p => p.City, sortable: true)
        .Field("countryCode", static p => p.CountryCode)
        .Field("starRating", static p => p.StarRating, sortable: true)
        .Field("status", static p => p.Status)
        .Field("updatedAt", static p => p.UpdatedAt, sortable: true)
        .DefaultOrderBy("name");
}

public sealed class ListPropertiesHandler(IStaysDb db) : IRequestHandler<ListProperties, KeysetPage<PropertySummary>>
{
    public async Task<KeysetPage<PropertySummary>> Handle(ListProperties request, CancellationToken cancellationToken)
    {
        IQueryable<StayProperty> visible = db.Properties.AsNoTracking();

        if (request.OwnOnly)
        {
            Guid partner = request.Caller.PartnerId ?? throw StayErrors.PropertyNotFound();
            visible = visible.Where(p => p.PartnerId == partner);
        }
        else
        {
            visible = visible.Where(static p => p.Status == PropertyStatus.Published);
        }

        IQueryable<PropertyRow> rows = visible.Select(static p => new PropertyRow
        {
            Id = p.Id,
            Name = p.Name,
            City = p.City,
            CountryCode = p.CountryCode,
            StarRating = p.StarRating,
            Status = p.Status,
            UpdatedAt = p.UpdatedAt,
        });

        List<PropertyRow> page = await request.Query.ApplyTo(rows).ToListAsync(cancellationToken);

        return request.Query.ToPage(
            page,
            static r => new PropertySummary(r.Id, r.Name, r.City, r.CountryCode, r.StarRating, r.Status, r.UpdatedAt));
    }
}
