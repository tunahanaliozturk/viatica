using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;
using Relevox;
using Viatica.Flights.Application;
using Viatica.Flights.Application.Features.Orders;
using Viatica.Flights.Application.Features.Reprice;
using Viatica.Flights.Application.Features.Search;
using Viatica.Flights.Application.Offers;
using Viatica.Flights.Domain;
using Viatica.Kernel;
using Viatica.Kernel.Querying;
using Viatica.Kernel.Web;

namespace Viatica.Flights.Api;

/// <summary>
/// The HTTP surface. Each handler's union result is switched here, so every case has a status code and a body the
/// compiler makes sure exists (ADR 0001). Search, paging and re-price are open to anyone: anonymous visitors search
/// too (requirements, section 3). Rate limits belong to the gateway.
/// </summary>
public static class FlightEndpoints
{
    public static void MapFlightEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder flights = app.MapGroup("/flights").WithTags("Flights");

        flights.MapPost("/searches", CreateSearchAsync)
            .WithSummary("Search every airline at once (FLT-1)")
            .WithDescription("Answers within the search deadline. Airlines that were too slow or failed are named in incompleteSuppliers.");

        flights.MapGet("/searches/{searchId:guid}", GetSearchAsync)
            .WithSummary("A page of a search already made (FLT-2)")
            .WithDescription("Reads the cache and never calls an airline. Follow nextLink for the next page.");

        flights.MapPost("/offers/{offerId}/price", RepriceAsync)
            .WithSummary("Re-price an offer with the airline (FLT-3)")
            .WithDescription("Required before an offer goes into a trip. A changed price is returned, never applied silently.");

        flights.MapGet("/orders/{orderId:guid}", GetOrderAsync)
            .RequireAuthorization(Policies.Admin)
            .WithSummary("An order as operators see it; no passenger data");
    }

    private static async Task<IResult> CreateSearchAsync(
        SearchFlightsRequest body, HttpContext http, ISender sender, CancellationToken cancellationToken)
    {
        SearchFlightsResult result = await sender.Send(body.ToCommand(), cancellationToken);

        switch (result)
        {
            case FlightSearchPage page:
                SkipTokenCodec codec = http.RequestServices.GetRequiredService<SkipTokenCodec>();
                string location = $"{http.Request.PathBase}/flights/searches/{page.SearchId}";
                string? nextLink = page.Offers.NextPosition is { } position ? FirstNextLink(http.Request, codec, location, position) : null;
                return TypedResults.Created(
                    $"{location}?api-version={http.Request.Query["api-version"]}",
                    FlightSearchResponse.From(page, nextLink));

            case NoSuppliersAnswered none:
                return ErrorResult.Unavailable(
                    FlightErrorCodes.NoSuppliersAvailable,
                    "No airline answered in time. Try again in a moment.",
                    [.. none.IncompleteSuppliers.Select(static s => new ApiError(s.Reason.ToString(), $"Airline {s.Airline} did not answer.") { Target = s.Airline })]);

            default:
                throw new UnreachableException("A search result was never set.");
        }
    }

    private static async Task<Ok<FlightSearchResponse>> GetSearchAsync(
        Guid searchId, HttpContext http, ISender sender, CancellationToken cancellationToken)
    {
        CollectionQuery<OfferRow> query = http.BindCollectionQuery(OfferRow.Fields);
        FlightSearchPage page = await sender.Send(new GetSearchQuery(searchId, query), cancellationToken);

        PagedResponse<FlightOffer> paged = http.Page(page.Offers).Value!;
        return TypedResults.Ok(FlightSearchResponse.From(page, paged.NextLink));
    }

    private static async Task<IResult> RepriceAsync(string offerId, ISender sender, CancellationToken cancellationToken)
    {
        RepriceResult result = await sender.Send(new RepriceOfferCommand(offerId), cancellationToken);

        return result switch
        {
            RepriceUnchanged unchanged => TypedResults.Ok(new RepriceResponse("Unchanged", unchanged.Offer, null)),
            RepriceChanged changed => TypedResults.Ok(new RepriceResponse(FlightErrorCodes.PriceChanged, changed.Offer, changed.PreviousTotal)),
            RepriceOfferExpired => ErrorResult.Conflict(FlightErrorCodes.OfferExpired, "The offer is no longer available. Search again."),
            RepriceSupplierUnavailable => ErrorResult.Unavailable(FlightErrorCodes.SupplierUnavailable, "The airline could not be asked. Try again in a moment."),
            null => throw new UnreachableException("A reprice result was never set."),
        };
    }

    private static async Task<Ok<FlightOrderView>> GetOrderAsync(
        Guid orderId, HttpContext http, ISender sender, CancellationToken cancellationToken)
    {
        FlightOrderView order = await sender.Send(new GetFlightOrderQuery(orderId), cancellationToken);
        http.Response.SetETag(order.Version);
        return TypedResults.Ok(order);
    }

    /// <summary>
    /// The first page of a new search is answered by a POST, but its <c>nextLink</c> must be a GET of the search.
    /// <c>Paging.Page</c> builds links from the current request, so this builds the same link by hand: the GET's
    /// path, the same <c>api-version</c>, and a skiptoken signed for exactly that query.
    /// </summary>
    private static string FirstNextLink(HttpRequest request, SkipTokenCodec codec, string searchPath, IReadOnlyList<string> position)
    {
        StringValues version = request.Query["api-version"];
        string token = codec.Encode(
            position,
            SkipTokenCodec.NormalizeQuery(searchPath, [new KeyValuePair<string, StringValues>("api-version", version)]));

        return new StringBuilder()
            .Append(request.Scheme).Append("://").Append(request.Host.ToUriComponent())
            .Append(searchPath)
            .Append("?api-version=").Append(Uri.EscapeDataString(version.ToString()))
            .Append("&$skiptoken=").Append(token)
            .ToString();
    }
}

/// <summary>An error envelope with a status the shared categories do not cover (503), or a plain 409 for a union case.</summary>
internal sealed class ErrorResult(int statusCode, ApiError error, int? retryAfterSeconds = null) : IResult
{
    public static ErrorResult Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, new ApiError(code, message));

    public static ErrorResult Unavailable(string code, string message, IReadOnlyList<ApiError>? details = null) =>
        new(StatusCodes.Status503ServiceUnavailable, new ApiError(code, message) { Details = details }, retryAfterSeconds: 5);

    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (retryAfterSeconds is { } seconds)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return ErrorResponses.WriteAsync(httpContext, statusCode, error);
    }
}
