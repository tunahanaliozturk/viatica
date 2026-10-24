using Viatica.Contracts;
using Viatica.Flights.Domain;
using Viatica.Kernel;

namespace Viatica.Flights.Application.Orders;

/// <summary>
/// The events an order's state maps to, built in one place so the first answer and every repeat of it are the same
/// event: a redelivered request is answered with what the first one produced.
/// </summary>
internal static class OrderEvents
{
    public static Price ToPrice(Money money) => new(money.Amount, money.Currency);

    public static FlightOrderCreated Created(FlightOrder order) => new(
        order.Id,
        order.TripId,
        order.ItemId,
        order.AirlineCode,
        order.BookingReference,
        order.TicketingTimeLimit,
        ToPrice(order.Total));

    public static FlightTicketed Ticketed(FlightOrder order) => new(order.Id, order.TripId, order.ItemId, order.TicketNumbers);

    public static FlightOrderVoided Voided(FlightOrder order) => new(order.Id, order.TripId, order.ItemId);

    public static FlightOrderCancelled Cancelled(FlightOrder order) =>
        new(order.Id, order.TripId, order.ItemId, ToPrice(order.Refund ?? Money.Zero(order.Currency)));
}

/// <summary>
/// The airline could not be reached for something that has to happen. Thrown so the consumer retries with backoff
/// and, after its attempt limit, parks the message: a void or a cancel is never answered with a guess.
/// </summary>
public sealed class SupplierUnavailableException(string operation) : Exception($"The airline could not complete {operation}.");
