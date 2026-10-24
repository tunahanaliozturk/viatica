using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Cars.Domain;
using Viatica.Kernel;

namespace Viatica.Cars.Application.Features.PutCategory;

/// <summary>Creates or replaces a car category under a client chosen id. See <c>PutLocation</c> for the version rule.</summary>
public sealed record PutCategory(
    Guid PartnerId,
    Guid Id,
    uint? ExpectedVersion,
    string Acriss,
    string Name,
    int Seats,
    Transmission Transmission,
    FuelType Fuel,
    int Bags,
    int MinimumDriverAge,
    Uri? ImageUrl) : IRequest<Versioned<CategoryView>>;

public sealed class PutCategoryValidator : AbstractValidator<PutCategory>
{
    public PutCategoryValidator()
    {
        RuleFor(static c => CarCategory.IsAcriss(c.Acriss), "acriss is a four letter ACRISS code.", nameof(PutCategory.Acriss))
            .WithErrorCode("InvalidAcriss");
        RuleFor(static c => !string.IsNullOrWhiteSpace(c.Name) && c.Name.Length <= 120, "name is required, at most 120 characters.", nameof(PutCategory.Name))
            .WithErrorCode("InvalidName");
        RuleFor(static c => c.Seats is >= 1 and <= 20, "seats is 1 to 20.", nameof(PutCategory.Seats))
            .WithErrorCode("InvalidSeats");
        RuleFor(static c => c.Bags is >= 0 and <= 20, "bags is 0 to 20.", nameof(PutCategory.Bags))
            .WithErrorCode("InvalidBags");
        RuleFor(static c => c.MinimumDriverAge is >= 18 and <= 99, "minimumDriverAge is 18 to 99.", nameof(PutCategory.MinimumDriverAge))
            .WithErrorCode("InvalidMinimumDriverAge");
        RuleFor(static c => Enum.IsDefined(c.Transmission), "transmission is not known.", nameof(PutCategory.Transmission))
            .WithErrorCode("InvalidTransmission");
        RuleFor(static c => Enum.IsDefined(c.Fuel), "fuel is not known.", nameof(PutCategory.Fuel))
            .WithErrorCode("InvalidFuel");
        // An image is shown in a browser, so only http(s) is useful; a javascript: or file: URL is refused outright.
        RuleFor(
            static c => c.ImageUrl is null || (c.ImageUrl.IsAbsoluteUri && c.ImageUrl.Scheme is "http" or "https"),
            "imageUrl is an absolute http or https URL.",
            nameof(PutCategory.ImageUrl)).WithErrorCode("InvalidImageUrl");
    }
}

public sealed class PutCategoryHandler(ICarsDb db, TimeProvider clock) : IRequestHandler<PutCategory, Versioned<CategoryView>>
{
    public async Task<Versioned<CategoryView>> Handle(PutCategory request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        CarCategory? category = await db.Categories.FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken);

        if (category is not null && category.PartnerId != request.PartnerId)
        {
            throw CarErrors.CategoryNotFound();
        }

        bool created = category is null;
        if (category is null)
        {
            if (request.ExpectedVersion is not null)
            {
                throw ApiErrorException.PreconditionFailed();
            }

            category = CarCategory.Create(
                request.Id, request.PartnerId, request.Acriss, request.Name, request.Seats, request.Transmission,
                request.Fuel, request.Bags, request.MinimumDriverAge, request.ImageUrl, now);
            db.Categories.Add(category);
        }
        else
        {
            if (request.ExpectedVersion is { } expected && expected != category.Version)
            {
                throw ApiErrorException.PreconditionFailed();
            }

            category.Revise(
                request.Acriss, request.Name, request.Seats, request.Transmission, request.Fuel, request.Bags,
                request.MinimumDriverAge, request.ImageUrl, now);
        }

        return await db.InTransactionAsync(
            async transaction =>
            {
                // The category is saved first: the snapshots below read published categories from the database.
                await db.SaveChangesAsync(cancellationToken);
                if (category.Status == ListingStatus.Published)
                {
                    await Announcements.PartnerListingsAsync(db, request.PartnerId, now, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                }

                return new Versioned<CategoryView>(CategoryView.From(category), category.Version, created);
            },
            cancellationToken);
    }
}
