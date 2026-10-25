using System.Globalization;
using System.Text.Json;
using Relevox;
using Viatica.Booking.Application;
using Viatica.Booking.Application.Features.Checkout;
using Viatica.Kernel;
using Viatica.Kernel.Web;

namespace Viatica.Booking.Api.Endpoints;

/// <summary>
/// Checkout (BKG-2): <c>PUT</c> with the trip's own id, answered by an operation to poll, and a server sent event
/// stream so the web app does not have to.
/// </summary>
internal static class CheckoutEndpoints
{
    public static void MapCheckoutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/trips/{tripId:guid}/checkout", async (Guid tripId, TravellerDetails body, HttpContext http, ISender sender, CancellationToken cancellationToken) =>
            {
                Operation operation = await sender.Send(new StartCheckout(tripId, http.ToCaller(), body), cancellationToken);
                return http.Accepted(Absolute(http, operation), $"/operations/{tripId}");
            })
            .RequireAuthorization(Policies.Traveller)
            .WithTags("Checkout")
            .WithSummary("Check out a trip: hold, authorise, ticket, commit, capture (BKG-2, BKG-3)")
            .WithDescription("The trip id is the idempotency key. The same body again changes nothing; another body for the same trip is 409 CheckoutConflict. Answers 202 with an Operation-Location.");

        app.MapGet("/operations/{id:guid}", async (Guid id, HttpContext http, ISender sender, CancellationToken cancellationToken) =>
                TypedResults.Ok(Absolute(http, await sender.Send(new GetOperation(id, http.ToCaller()), cancellationToken))))
            .AllowAnonymous()
            .WithTags("Checkout")
            .WithSummary("Where a checkout or a cancellation stands")
            .WithDescription("The id is the trip's (checkout) or the item's (cancellation).");

        app.MapGet("/trips/{tripId:guid}/events", StreamProgressAsync)
            .AllowAnonymous()
            .WithTags("Checkout")
            .WithSummary("Server sent events: the trip every time it changes, until the checkout ends")
            .WithDescription("Each event is the trip as GET /trips/{id} returns it. The event id is the trip's revision; reconnect with Last-Event-ID to skip what you have.");
    }

    /// <summary>The operation's <c>resourceLocation</c> is a path inside the service; clients get the absolute URL, as for every other link.</summary>
    private static Operation Absolute(HttpContext http, Operation operation) =>
        operation.ResourceLocation is { } path ? operation with { ResourceLocation = Operations.OperationLocation(http.Request, path) } : operation;

    private static async Task StreamProgressAsync(
        Guid tripId, HttpContext http, ISender sender, BookingOptions options, CancellationToken cancellationToken)
    {
        // Authorised once, here; the stream then reads the trip by id. A trip that is not the caller's answers 404
        // before a single byte of the stream is written.
        TripProgress current = await sender.Send(new OpenProgress(tripId, http.ToCaller()), cancellationToken);

        int sent = int.TryParse(http.Request.Headers["Last-Event-ID"], NumberStyles.None, CultureInfo.InvariantCulture, out int last) ? last : -1;

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        // Headers go out now, so a client waiting for the stream to open does not wait for the first change.
        await http.Response.StartAsync(cancellationToken);

        DateTimeOffset lastWrite = DateTimeOffset.UtcNow;
        TripProgress? progress = current;

        try
        {
            while (true)
            {
                if (progress is not null && progress.Revision > sent)
                {
                    await WriteAsync(http, progress, cancellationToken);
                    sent = progress.Revision;
                    lastWrite = DateTimeOffset.UtcNow;
                }
                else if (progress is null && DateTimeOffset.UtcNow - lastWrite > options.ProgressKeepAlive)
                {
                    // A comment line keeps proxies from closing a stream that has had nothing to say for a while.
                    await http.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
                    await http.Response.Body.FlushAsync(cancellationToken);
                    lastWrite = DateTimeOffset.UtcNow;
                }

                // A trip that has ended and has told the client so closes the stream, whether the client just read the last
                // event or reconnected with it already in hand.
                if (current.Final && sent >= current.Revision)
                {
                    return;
                }

                await Task.Delay(options.ProgressPollInterval, cancellationToken);
                progress = await sender.Send(new PollProgress(tripId, sent), cancellationToken);
                current = progress ?? current;
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away. Nothing to report.
        }
    }

    private static async Task WriteAsync(HttpContext http, TripProgress progress, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(progress.Trip, BookingJsonContext.Default.TripView);
        await http.Response.WriteAsync(
            string.Create(CultureInfo.InvariantCulture, $"id: {progress.Revision}\nevent: trip\ndata: {json}\n\n"), cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
    }
}
