using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionGuard.DependencyInjection;
using Viatica.Cars.Application.Features.GetQuote;
using Viatica.Cars.Application.Features.PutCategory;
using Viatica.Cars.Application.Features.PutLocation;
using Viatica.Cars.Application.Features.SetInventory;
using Viatica.Cars.Application.Quotes;

namespace Viatica.Cars.Application;

public static class CarsApplication
{
    /// <summary>
    /// The request validators and the quote token codec. Handlers are not registered here: Relevox generates their
    /// registration in the host, and the Kafka subscription registers the integration event handlers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="quoteSigningKey">The HMAC key for quote tokens, at least 32 bytes, Cars only.</param>
    public static IServiceCollection AddCarsApplication(this IServiceCollection services, byte[] quoteSigningKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(quoteSigningKey);

        services.AddValidator<PutLocation, PutLocationValidator>();
        services.AddValidator<PutCategory, PutCategoryValidator>();
        services.AddValidator<Features.PartnerSettings.PutPartnerSettings, Features.PartnerSettings.PutPartnerSettingsValidator>();
        services.AddValidator<SetInventory, SetInventoryValidator>();
        services.AddValidator<GetQuote, GetQuoteValidator>();

        services.AddSingleton(sp => new QuoteTokenCodec(quoteSigningKey, sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
