using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Cars.Domain;

namespace Viatica.Cars.Application.Features.GetCategory;

public sealed record GetCategory(Guid PartnerId, Guid Id) : IRequest<Versioned<CategoryView>>;

public sealed class GetCategoryHandler(ICarsDb db) : IRequestHandler<GetCategory, Versioned<CategoryView>>
{
    public async Task<Versioned<CategoryView>> Handle(GetCategory request, CancellationToken cancellationToken)
    {
        CarCategory category = await db.Categories.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.Id && c.PartnerId == request.PartnerId, cancellationToken)
            ?? throw CarErrors.CategoryNotFound();

        return new Versioned<CategoryView>(CategoryView.From(category), category.Version);
    }
}
