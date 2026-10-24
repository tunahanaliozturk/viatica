using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Cars.Domain;
using Viatica.Kernel.Querying;

namespace Viatica.Cars.Application.Features.ListCategories;

/// <summary>A page of the partner's own car categories.</summary>
public sealed record ListCategories(Guid PartnerId, CollectionQuery<CarCategory> Query) : IRequest<KeysetPage<CategoryView>>
{
    public static QueryFields<CarCategory> Fields { get; } = new QueryFields<CarCategory>()
        .Key("id", static c => c.Id)
        .Field("acriss", static c => c.Acriss, sortable: true)
        .Field("name", static c => c.Name, sortable: true)
        .Field("status", static c => c.Status)
        .Field("seats", static c => c.Seats, sortable: true)
        .DefaultOrderBy("acriss");
}

public sealed class ListCategoriesHandler(ICarsDb db) : IRequestHandler<ListCategories, KeysetPage<CategoryView>>
{
    public async Task<KeysetPage<CategoryView>> Handle(ListCategories request, CancellationToken cancellationToken)
    {
        List<CarCategory> rows = await request.Query
            .ApplyTo(db.Categories.AsNoTracking().Where(c => c.PartnerId == request.PartnerId))
            .ToListAsync(cancellationToken);

        return request.Query.ToPage(rows, CategoryView.From);
    }
}
