using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionGuard.DependencyInjection;
using Viatica.Stays.Application.Features.Holds;
using Viatica.Stays.Application.Features.Nights;
using Viatica.Stays.Application.Features.Properties;
using Viatica.Stays.Application.Features.Quotes;
using Viatica.Stays.Application.Features.RoomTypes;
using Viatica.Stays.Application.Inventory;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application;

public static class StaysApplication
{
    /// <summary>
    /// Registers what the use cases need that is not a handler (Relevox finds those at build time): the validators,
    /// the quote token signer, and the services the hold handlers share.
    /// </summary>
    /// <param name="quoteKey">The HMAC key for quote tokens. Stays-only; Cars has its own.</param>
    public static IServiceCollection AddStaysApplication(this IServiceCollection services, ReadOnlySpan<byte> quoteKey)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new QuoteTokens(quoteKey));

        services.AddScoped<NightLedger>();
        services.AddScoped<HoldTransitions>();
        services.AddScoped<ExpireDueHolds>();

        services.AddValidator<CreateProperty, CreatePropertyValidator>();
        services.AddValidator<UpdateProperty, UpdatePropertyValidator>();
        services.AddValidator<PutRoomType, PutRoomTypeValidator>();
        services.AddValidator<UpdateRoomType, UpdateRoomTypeValidator>();
        services.AddValidator<SetNights, SetNightsValidator>();
        services.AddValidator<GetNights, GetNightsValidator>();
        services.AddValidator<CreateQuote, CreateQuoteValidator>();

        return services;
    }
}
