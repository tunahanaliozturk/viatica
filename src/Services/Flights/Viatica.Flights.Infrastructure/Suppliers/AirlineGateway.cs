using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Viatica.Flights.Application;
using Viatica.Flights.Application.Offers;
using Viatica.Flights.Application.Suppliers;
using Viatica.Flights.Domain;
using Viatica.Kernel;
using Viatica.Kernel.Pii;

namespace Viatica.Flights.Infrastructure.Suppliers;

/// <summary>
/// Talks to the airlines over HTTP, one named client per airline, and turns their foreign contract into the
/// platform's outcomes. Nothing outside this class knows an airline's field names, status codes or error texts.
/// </summary>
/// <remarks>
/// <para>
/// Resilience sits in each client's pipeline (see <see cref="AirlineClients"/>): a timeout per attempt and a circuit
/// breaker per airline, and no retry. Searches are bounded by a deadline a retry could not meet, and order creation
/// must never be sent blindly twice; a timed out creation is looked up by key instead.
/// </para>
/// <para>
/// A failure is classified by whether the airline might have acted on the request. A timeout, a dropped connection
/// and a 500 might have; an open circuit and a 503 certainly did not. The order handler relies on that distinction.
/// </para>
/// </remarks>
public sealed partial class AirlineGateway(
    IHttpClientFactory clients,
    IOptions<SuppliersOptions> options,
    FlightsOptions flights,
    TimeProvider time,
    ILogger<AirlineGateway> logger) : IAirlineShopping, IAirlineOrdering
{
    internal static readonly HttpRequestOptionsKey<TimeSpan> TimeoutKey = new("flights.attempt-timeout");

    public IReadOnlyList<string> Airlines { get; } = [.. options.Value.Airlines.Select(static a => a.Code)];

    public async Task<SearchOutcome> SearchAsync(string airline, SearchCriteria criteria, CancellationToken cancellationToken)
    {
        var body = new SearchRequestDto(
            criteria.Origin.Value,
            criteria.Destination.Value,
            criteria.DepartureDate,
            criteria.ReturnDate,
            Mix(criteria.Passengers),
            criteria.Cabin.ToString());

        // The search budget is the deadline itself: the airline's client gives up exactly when the fan-out would.
        Reply<SearchResponseDto> reply = await SendAsync(
            airline, "search", HttpMethod.Post, "search", AirlineJson.Default.SearchRequestDto, body,
            AirlineJson.Default.SearchResponseDto, flights.SearchDeadline, cancellationToken);

        if (reply.Failure is { } failure)
        {
            return failure;
        }

        if (reply.Status != HttpStatusCode.OK || reply.Body is null)
        {
            return new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: true);
        }

        List<SupplierOffer> offers = [];
        foreach (OfferDto dto in reply.Body.Offers)
        {
            if (TryMap(airline, dto) is { } offer)
            {
                offers.Add(offer);
            }
            else
            {
                LogUnmappableOffer(logger, airline);
            }
        }

        return new SupplierOffers(offers);
    }

    public async Task<RepriceOutcome> RepriceAsync(string airline, string supplierOfferId, CancellationToken cancellationToken)
    {
        Reply<RepriceResponseDto> reply = await SendAsync<object, RepriceResponseDto>(
            airline, "reprice", HttpMethod.Post, $"offers/{Uri.EscapeDataString(supplierOfferId)}/price", null, null,
            AirlineJson.Default.RepriceResponseDto, null, cancellationToken);

        if (reply.Failure is { } failure)
        {
            return failure;
        }

        if (reply.Status == HttpStatusCode.OK && reply.Body is { } body && TryMap(airline, body.Offer) is { } offer)
        {
            return new OfferRepriced(offer, body.PreviousTotal);
        }

        // 410 OFFER_EXPIRED, 404 OFFER_NOT_FOUND and 409 NO_AVAILABILITY all mean the same to a traveller: pick again.
        return reply.Status is HttpStatusCode.Gone or HttpStatusCode.NotFound or HttpStatusCode.Conflict
            ? new OfferGone()
            : new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: true);
    }

    public async Task<CreateOrderOutcome> CreateOrderAsync(string airline, NewSupplierOrder order, CancellationToken cancellationToken)
    {
        var body = new OrderCreateDto(order.SupplierOfferId, order.IdempotencyKey, [.. order.Passengers.Select(ToDto)]);
        Reply<OrderDto> reply = await SendAsync(
            airline, "create_order", HttpMethod.Post, "orders", AirlineJson.Default.OrderCreateDto, body,
            AirlineJson.Default.OrderDto, null, cancellationToken);

        if (reply.Failure is { } failure)
        {
            return failure;
        }

        // 201 for a new order, 200 when the key was seen before: the same order either way.
        if (reply.Status is HttpStatusCode.Created or HttpStatusCode.OK && reply.Body is { } created && TryMap(created) is { } mapped)
        {
            return new OrderCreated(mapped);
        }

        return reply.ErrorCode switch
        {
            "NO_AVAILABILITY" => new OrderRefused(RefusalReason.SoldOut),
            "OFFER_EXPIRED" or "OFFER_NOT_FOUND" => new OrderRefused(RefusalReason.OfferExpired),
            "INVALID_REQUEST" => new OrderRefused(RefusalReason.InvalidPassengers),
            "IDEMPOTENCY_KEY_REUSED" => new OrderRefused(RefusalReason.Other),
            _ => new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: true),
        };
    }

    public async Task<FindOrderOutcome> FindOrderAsync(string airline, string idempotencyKey, CancellationToken cancellationToken)
    {
        Reply<OrderDto> reply = await SendAsync<object, OrderDto>(
            airline, "find_order", HttpMethod.Get, $"orders?idempotencyKey={Uri.EscapeDataString(idempotencyKey)}", null, null,
            AirlineJson.Default.OrderDto, null, cancellationToken);

        if (reply.Failure is { } failure)
        {
            return failure;
        }

        if (reply.Status == HttpStatusCode.OK && reply.Body is { } found && TryMap(found) is { } mapped)
        {
            return new OrderFound(mapped);
        }

        return reply.Status == HttpStatusCode.NotFound && reply.ErrorCode == "ORDER_NOT_FOUND"
            ? new NoSuchOrder()
            : new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: true);
    }

    public async Task<TicketingOutcome> TicketAsync(string airline, string bookingReference, CancellationToken cancellationToken)
    {
        Reply<OrderDto> reply = await OrderActionAsync(airline, "ticket", bookingReference, cancellationToken);

        if (reply.Failure is { } failure)
        {
            return failure;
        }

        if (reply.Status == HttpStatusCode.OK && reply.Body is { } order)
        {
            return new TicketsIssued([.. order.TicketNumbers]);
        }

        return reply.ErrorCode switch
        {
            "TICKETING_REFUSED" or "TICKETING_TIME_LIMIT_EXPIRED" or "ORDER_CLOSED" =>
                new TicketsRefused($"{reply.ErrorCode}: {reply.ErrorText}"),
            _ => new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: true),
        };
    }

    public async Task<VoidOutcome> VoidAsync(string airline, string bookingReference, CancellationToken cancellationToken)
    {
        Reply<OrderDto> reply = await OrderActionAsync(airline, "void", bookingReference, cancellationToken);

        if (reply.Failure is { } failure)
        {
            return failure;
        }

        if (reply.Status == HttpStatusCode.OK)
        {
            return new OrderVoided();
        }

        return reply.ErrorCode switch
        {
            "VOID_WINDOW_CLOSED" => new VoidWindowClosed(),
            "ORDER_NOT_FOUND" or "ORDER_CLOSED" => new OrderGone(),
            _ => new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: true),
        };
    }

    public async Task<CancelOutcome> CancelAsync(string airline, string bookingReference, CancellationToken cancellationToken)
    {
        Reply<OrderDto> reply = await OrderActionAsync(airline, "cancel", bookingReference, cancellationToken);

        if (reply.Failure is { } failure)
        {
            return failure;
        }

        if (reply.Status == HttpStatusCode.OK && reply.Body is { } order && TryMap(order) is { } mapped)
        {
            return new OrderCancelled(mapped.Refund ?? Money.Zero(mapped.Total.Currency));
        }

        return reply.ErrorCode switch
        {
            "ORDER_NOT_TICKETED" => new NotTicketed(),
            "ORDER_NOT_FOUND" or "ORDER_CLOSED" => new OrderGone(),
            _ => new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: true),
        };
    }

    private Task<Reply<OrderDto>> OrderActionAsync(string airline, string action, string bookingReference, CancellationToken cancellationToken) =>
        SendAsync<object, OrderDto>(
            airline, action, HttpMethod.Post, $"orders/{Uri.EscapeDataString(bookingReference)}/{action}", null, null,
            AirlineJson.Default.OrderDto, null, cancellationToken);

    /// <summary>One airline call: send, time it, classify the failure, and read the body whatever the status.</summary>
    private async Task<Reply<TResponse>> SendAsync<TRequest, TResponse>(
        string airline,
        string operation,
        HttpMethod method,
        string path,
        JsonTypeInfo<TRequest>? requestType,
        TRequest? request,
        JsonTypeInfo<TResponse> responseType,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        using Activity? activity = FlightsTelemetry.Source.StartActivity($"flights.supplier.{operation}", ActivityKind.Client);
        activity?.SetTag("flights.airline", airline);

        long started = time.GetTimestamp();
        using var message = new HttpRequestMessage(method, path);
        message.Options.Set(TimeoutKey, timeout ?? options.Value.RequestTimeout);
        if (request is not null && requestType is not null)
        {
            message.Content = JsonContent.Create(request, requestType);
        }

        string outcome = "error";
        try
        {
            using HttpClient client = clients.CreateClient(AirlineClients.NameOf(airline));
            using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);

            Reply<TResponse> reply = await ReadAsync(response, responseType, cancellationToken);
            outcome = reply.Failure is { } failure ? FailureName(failure) : ((int)reply.Status).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return reply;
        }
        catch (BrokenCircuitException)
        {
            outcome = "circuit_open";
            return new Reply<TResponse>(new SupplierFailure(FailureKind.CircuitOpen, MayHaveReachedAirline: false));
        }
        catch (TimeoutRejectedException)
        {
            outcome = "timeout";
            return new Reply<TResponse>(new SupplierFailure(FailureKind.Timeout, MayHaveReachedAirline: true));
        }
        catch (HttpRequestException)
        {
            outcome = "connection_error";
            return new Reply<TResponse>(new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: true));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, not the caller hanging up.
            outcome = "timeout";
            return new Reply<TResponse>(new SupplierFailure(FailureKind.Timeout, MayHaveReachedAirline: true));
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            double elapsedMs = time.GetElapsedTime(started).TotalMilliseconds;
            var airlineTag = new KeyValuePair<string, object?>("airline", airline);
            var operationTag = new KeyValuePair<string, object?>("operation", operation);
            FlightsTelemetry.SupplierCalls.Add(1, airlineTag, operationTag, new KeyValuePair<string, object?>("outcome", outcome));
            FlightsTelemetry.SupplierLatency.Record(elapsedMs, airlineTag, operationTag);
        }
    }

    private static async Task<Reply<T>> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;

        // 503 is the airline saying it is down before it did anything. Every other 5xx may have happened.
        if (status == 503)
        {
            return new Reply<T>(new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: false));
        }

        if (status == 504)
        {
            return new Reply<T>(new SupplierFailure(FailureKind.Timeout, MayHaveReachedAirline: true));
        }

        if (status >= 500)
        {
            return new Reply<T>(new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: true));
        }

        try
        {
            if (response.IsSuccessStatusCode)
            {
                return new Reply<T>(response.StatusCode, await response.Content.ReadFromJsonAsync(type, cancellationToken));
            }

            ErrorResponseDto? error = await response.Content.ReadFromJsonAsync(AirlineJson.Default.ErrorResponseDto, cancellationToken);
            ErrorItemDto? first = error?.Errors.FirstOrDefault();
            return new Reply<T>(response.StatusCode, default, first?.Code, first?.DescText);
        }
        catch (System.Text.Json.JsonException)
        {
            // An airline that answers with something unreadable is an airline in trouble, not a "no".
            return new Reply<T>(new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: true));
        }
    }

    private static string FailureName(SupplierFailure failure) => failure.Kind switch
    {
        FailureKind.Timeout => "timeout",
        FailureKind.CircuitOpen => "circuit_open",
        _ => "error",
    };

    private static PassengerMixDto Mix(PassengerMix mix) => new(mix.Adults, mix.Children, mix.Infants);

    private static PassengerDto ToDto(PassengerDetails passenger) => new(
        passenger.Type switch
        {
            PassengerType.Adult => "ADT",
            PassengerType.Child => "CHD",
            _ => "INF",
        },
        passenger.GivenName,
        passenger.Surname,
        passenger.BirthDate);

    private static SupplierOffer? TryMap(string airline, OfferDto dto)
    {
        if (!Enum.TryParse(dto.Cabin, ignoreCase: true, out Cabin cabin) || dto.Segments.Count == 0 || !IsMoney(dto.Price))
        {
            return null;
        }

        var offer = new FlightOffer(
            Id: "",
            airline,
            dto.FareClass,
            cabin,
            dto.Refundable,
            new Money(dto.Price.Total, dto.Price.Currency),
            dto.SeatsLeft,
            dto.ExpiresAt,
            [
                .. dto.Segments.Select(static s => new FlightSegment(
                    s.FlightNumber, s.Origin, s.Destination, s.DepartureLocal, s.ArrivalLocal, s.DurationMinutes, s.Aircraft)),
            ]);

        return new SupplierOffer(dto.OfferId, offer);
    }

    private static SupplierOrder? TryMap(OrderDto dto)
    {
        if (!Enum.TryParse(dto.Status, ignoreCase: true, out SupplierOrderStatus status) || !IsMoney(dto.Price))
        {
            return null;
        }

        Money total = new(dto.Price.Total, dto.Price.Currency);
        Money? refund = dto.RefundAmount is { } amount ? new Money(amount, dto.Price.Currency) : null;
        return new SupplierOrder(dto.BookingReference, dto.IdempotencyKey, status, total, dto.TicketingTimeLimit, dto.TicketNumbers, refund);
    }

    // Money refuses amounts with more decimals than the currency has; an airline's malformed price is a bad offer,
    // not an exception in the middle of a fan-out.
    private static bool IsMoney(PriceDto price) =>
        price.Currency is { Length: 3 } && decimal.Round(price.Total, Money.MinorUnitsOf(price.Currency)) == price.Total;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Airline {Airline} sent an offer this service could not read; it was dropped")]
    private static partial void LogUnmappableOffer(ILogger logger, string airline);

    /// <summary>What came back, or why nothing did.</summary>
    private readonly record struct Reply<T>(SupplierFailure? Failure, HttpStatusCode Status, T? Body, string? ErrorCode, string? ErrorText)
    {
        public Reply(SupplierFailure failure)
            : this(failure, default, default, null, null)
        {
        }

        public Reply(HttpStatusCode status, T? body, string? errorCode = null, string? errorText = null)
            : this(null, status, body, errorCode, errorText)
        {
        }
    }
}
