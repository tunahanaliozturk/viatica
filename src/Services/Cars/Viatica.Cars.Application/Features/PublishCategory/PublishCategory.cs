using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Cars.Domain;

namespace Viatica.Cars.Application.Features.PublishCategory;

/// <summary>
/// Adds a category to every published location of the partner, or takes it off. Each of those locations is
/// announced again, because the category is part of the location's snapshot.
/// </summary>
public sealed record PublishCategory(Guid PartnerId, Guid Id, bool Publish) : IRequest<Versioned<CategoryView>>;

public sealed class PublishCategoryHandler(ICarsDb db, TimeProvider clock) : IRequestHandler<PublishCategory, Versioned<CategoryView>>
{
    public async Task<Versioned<CategoryView>> Handle(PublishCategory request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        CarCategory category = await db.Categories
            .FirstOrDefaultAsync(c => c.Id == request.Id && c.PartnerId == request.PartnerId, cancellationToken)
            ?? throw CarErrors.CategoryNotFound();

        return await db.InTransactionAsync(
            async transaction =>
            {
                bool changed = request.Publish ? category.Publish(now) : category.Unpublish(now);
                await db.SaveChangesAsync(cancellationToken);
                if (changed)
                {
                    await Announcements.PartnerListingsAsync(db, request.PartnerId, now, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                }

                return new Versioned<CategoryView>(CategoryView.From(category), category.Version);
            },
            cancellationToken);
    }
}
