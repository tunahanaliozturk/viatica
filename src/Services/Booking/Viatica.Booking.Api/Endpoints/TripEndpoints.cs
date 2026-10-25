using Microsoft.AspNetCore.Http.HttpResults;
using Relevox;
using Viatica.Booking.Application;
using Viatica.Booking.Application.Features.Trips;
using Viatica.Kernel.Web;

namespace Viatica.Booking.Api.Endpoints;

/// <summary>
/// Building a trip (BKG-1). A trip is created by <c>PUT</c> with an id the client chose, so a retry is the same
/// request. Signed-in travellers own their trips; anyone else builds one with a <c>Trip-Access-Key</c> of their own
/// making and claims it at sign-in.
/// </summary>
internal static class TripEndpoints
{
    public static void MapTripEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder trips = app.MapGroup("/trips").WithTags("Trips");

        trips.MapPut("/{tripId:guid}", async (Guid tripId, HttpContext http, ISender sender, CancellationToken cancellationToken) =>
                Respond(http, await sender.Send(new CreateTrip(tripId, http.ToCaller()), cancellationToken), $"/trips/{tripId}"))
            .AllowAnonymous()
            .WithSummary("Start a draft trip under an id you chose (BKG-1)")
            .WithDescription("Signed in, the trip is yours. Anonymous, send a Trip-Access-Key only you know and claim the trip at sign-in. Repeating the request answers 200 with the trip.");

        trips.MapGet("/{tripId:guid}", async (Guid tripId, HttpContext http, ISender sender, CancellationToken cancellationToken) =>
                Respond(http, await sender.Send(new GetTrip(tripId, http.ToCaller()), cancellationToken), null))
            .AllowAnonymous()
            .WithSummary("A trip with its items, their states and what it costs now");

        trips.MapGet(string.Empty, async (HttpContext http, ISender sender, CancellationToken cancellationToken) =>
                http.Page(await sender.Send(new ListTrips(http.BindCollectionQuery(TripQueries.Fields), http.ToCaller()), cancellationToken)))
            .RequireAuthorization(Policies.Traveller)
            .WithSummary("The signed-in traveller's own trips, newest first (BKG-5)");

        trips.MapPut("/{tripId:guid}/items/{itemId:guid}", async (Guid tripId, Guid itemId, ItemSpec body, HttpContext http, ISender sender, CancellationToken cancellationToken) =>
                Respond(http, await sender.Send(new AddItem(tripId, itemId, body, http.ToCaller()), cancellationToken), $"/trips/{tripId}"))
            .AllowAnonymous()
            .WithSummary("Add a stay, a car or a flight to a draft trip")
            .WithDescription("A stay or a car carries its quote token; a flight carries the offer id Flights re-priced and the total the traveller was shown.");

        trips.MapDelete("/{tripId:guid}/items/{itemId:guid}", async (Guid tripId, Guid itemId, HttpContext http, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new RemoveItem(tripId, itemId, http.ToCaller()), cancellationToken);
                return TypedResults.NoContent();
            })
            .AllowAnonymous()
            .WithSummary("Take an item out of a draft trip");

        trips.MapPost("/{tripId:guid}/claim", async (Guid tripId, HttpContext http, ISender sender, CancellationToken cancellationToken) =>
                Respond(http, await sender.Send(new ClaimTrip(tripId, http.ToCaller()), cancellationToken), null))
            .RequireAuthorization(Policies.Traveller)
            .WithSummary("Take the trip you built before signing in")
            .WithDescription("Send the Trip-Access-Key you built it with. Once claimed the key stops working and the token is the way in.");
    }

    /// <summary>The ETag header, and 201 with a Location for a trip that was just created or an item added, 200 for everything else.</summary>
    private static Results<Created<TripView>, Ok<TripView>> Respond(HttpContext http, Resource<TripView> resource, string? location)
    {
        http.Response.SetETag(resource.Version);
        return resource.IsNew && location is not null
            ? TypedResults.Created(Operations.OperationLocation(http.Request, location), resource.Value)
            : TypedResults.Ok(resource.Value);
    }
}
