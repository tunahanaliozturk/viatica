using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionGuard.DependencyInjection;
using Viatica.Booking.Application.Features.Checkout;
using Viatica.Booking.Application.Features.Trips;

namespace Viatica.Booking.Application;

public static class BookingApplication
{
    /// <summary>
    /// The request validators, the saga's services and the vault that seals traveller data. Handlers are not registered
    /// here: Relevox generates their registration in the host, and the Kafka subscriptions register the integration
    /// event handlers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The saga's clocks.</param>
    /// <param name="passengerKey">The key shared with Flights, 32 bytes. Seals the passenger list on its way to the airline.</param>
    /// <param name="storageKey">Booking's own key, 32 bytes. Seals traveller data at rest.</param>
    public static IServiceCollection AddBookingApplication(
        this IServiceCollection services, BookingOptions options, byte[] passengerKey, byte[] storageKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(passengerKey);
        ArgumentNullException.ThrowIfNull(storageKey);

        services.AddValidator<CreateTrip, CreateTripValidator>();
        services.AddValidator<AddItem, AddItemValidator>();
        services.AddValidator<StartCheckout, StartCheckoutValidator>();

        services.AddSingleton(options);
        services.AddSingleton(new TravellerVault(passengerKey, storageKey));
        services.AddScoped<DirectivePublisher>();
        services.AddScoped<TripRunner>();
        services.AddScoped<ScheduledChecks>();
        return services;
    }
}
