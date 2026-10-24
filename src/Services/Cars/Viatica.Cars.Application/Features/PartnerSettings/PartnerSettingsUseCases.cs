using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Cars.Domain;
using Viatica.Kernel;

namespace Viatica.Cars.Application.Features.PartnerSettings;

/// <summary>The one-way fee and the young driver surcharge, in euros.</summary>
public sealed record PartnerSettingsView(Money OneWayFee, Money YoungDriverSurchargePerDay);

public sealed record GetPartnerSettings(Guid PartnerId) : IRequest<Versioned<PartnerSettingsView>>;

/// <summary>Sets both fees. <c>ExpectedVersion</c> is null for the first save and for "whatever is there".</summary>
public sealed record PutPartnerSettings(Guid PartnerId, uint? ExpectedVersion, Money OneWayFee, Money YoungDriverSurchargePerDay)
    : IRequest<Versioned<PartnerSettingsView>>;

public sealed class PutPartnerSettingsValidator : AbstractValidator<PutPartnerSettings>
{
    public PutPartnerSettingsValidator()
    {
        RuleFor(static c => IsFee(c.OneWayFee), "oneWayFee is 0 to 10000 EUR.", nameof(PutPartnerSettings.OneWayFee))
            .WithErrorCode("InvalidFee");
        RuleFor(static c => IsFee(c.YoungDriverSurchargePerDay), "youngDriverSurchargePerDay is 0 to 10000 EUR.", nameof(PutPartnerSettings.YoungDriverSurchargePerDay))
            .WithErrorCode("InvalidFee");
    }

    private static bool IsFee(Money fee) => fee.Currency == Money.Euro && fee.Amount is >= 0 and <= 10_000;
}

public sealed class GetPartnerSettingsHandler(ICarsDb db) : IRequestHandler<GetPartnerSettings, Versioned<PartnerSettingsView>>
{
    public async Task<Versioned<PartnerSettingsView>> Handle(GetPartnerSettings request, CancellationToken cancellationToken)
    {
        Domain.PartnerSettings? settings = await db.PartnerSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.PartnerId == request.PartnerId, cancellationToken);

        return settings is null
            ? new Versioned<PartnerSettingsView>(new PartnerSettingsView(Money.Eur(0m), Money.Eur(0m)), 0)
            : new Versioned<PartnerSettingsView>(ToView(settings), settings.Version);
    }

    internal static PartnerSettingsView ToView(Domain.PartnerSettings s) =>
        new(s.OneWayFee, s.YoungDriverSurchargePerDay);
}

public sealed class PutPartnerSettingsHandler(ICarsDb db) : IRequestHandler<PutPartnerSettings, Versioned<PartnerSettingsView>>
{
    public async Task<Versioned<PartnerSettingsView>> Handle(PutPartnerSettings request, CancellationToken cancellationToken)
    {
        Domain.PartnerSettings? settings = await db.PartnerSettings
            .FirstOrDefaultAsync(s => s.PartnerId == request.PartnerId, cancellationToken);

        bool created = settings is null;
        if (settings is null)
        {
            if (request.ExpectedVersion is not null)
            {
                throw ApiErrorException.PreconditionFailed();
            }

            settings = Domain.PartnerSettings.Defaults(request.PartnerId);
            db.PartnerSettings.Add(settings);
        }
        else if (request.ExpectedVersion is { } expected && expected != settings.Version)
        {
            throw ApiErrorException.PreconditionFailed();
        }

        settings.Change(request.OneWayFee, request.YoungDriverSurchargePerDay);
        await db.SaveChangesAsync(cancellationToken);
        return new Versioned<PartnerSettingsView>(GetPartnerSettingsHandler.ToView(settings), settings.Version, created);
    }
}
