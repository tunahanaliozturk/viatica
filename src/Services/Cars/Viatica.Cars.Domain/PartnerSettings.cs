using Moongazing.OrionGuard.Core;
using Viatica.Kernel;

namespace Viatica.Cars.Domain;

/// <summary>
/// The pricing a partner applies across all its locations: one flat fee for a one-way rental and a per-day
/// surcharge for young drivers. Both default to zero until the partner sets them.
/// </summary>
public sealed class PartnerSettings
{
    private PartnerSettings()
    {
    }

    public Guid PartnerId { get; private set; }

    public decimal OneWayFeeAmount { get; private set; }

    public decimal YoungDriverSurchargePerDayAmount { get; private set; }

    /// <summary>Postgres <c>xmin</c>: the row version behind the ETag.</summary>
    public uint Version { get; private set; }

    public Money OneWayFee => Money.Eur(OneWayFeeAmount);

    public Money YoungDriverSurchargePerDay => Money.Eur(YoungDriverSurchargePerDayAmount);

    public static PartnerSettings Defaults(Guid partnerId) => new() { PartnerId = partnerId };

    public void Change(Money oneWayFee, Money youngDriverSurchargePerDay)
    {
        ArgumentNullException.ThrowIfNull(oneWayFee);
        ArgumentNullException.ThrowIfNull(youngDriverSurchargePerDay);
        Ensure.That(oneWayFee.Amount, nameof(oneWayFee)).NotNegative();
        Ensure.That(youngDriverSurchargePerDay.Amount, nameof(youngDriverSurchargePerDay)).NotNegative();

        OneWayFeeAmount = oneWayFee.Amount;
        YoungDriverSurchargePerDayAmount = youngDriverSurchargePerDay.Amount;
    }
}
