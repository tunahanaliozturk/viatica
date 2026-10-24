using System.Diagnostics;
using Viatica.AirlineSim.Behaviour;

namespace Viatica.AirlineSim.Api;

internal static class SupplierApi
{
    /// <summary>
    /// The airline API. The airline code is the first path segment, as if each airline had its own host behind one
    /// gateway. Every call goes through the behaviour filter first, so outages and latency apply to all of them.
    /// </summary>
    public static void MapSupplierApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder airline = app.MapGroup("/ndc/{airline}").AddEndpointFilter(BehaveAsync);

        airline.MapPost("/search", Shopping.SearchAsync);
        airline.MapPost("/offers/{offerId}/price", Shopping.PriceAsync);
        airline.MapPost("/orders", Ordering.CreateAsync);
        airline.MapGet("/orders", Ordering.FindByIdempotencyKeyAsync);
        airline.MapGet("/orders/{bookingReference}", Ordering.GetAsync);
        airline.MapPost("/orders/{bookingReference}/ticket", Ordering.TicketAsync);
        airline.MapPost("/orders/{bookingReference}/void", Ordering.VoidAsync);
        airline.MapPost("/orders/{bookingReference}/cancel", Ordering.CancelAsync);
    }

    private static async ValueTask<object?> BehaveAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;
        string code = http.GetRouteValue("airline") as string ?? "";

        if (http.RequestServices.GetRequiredService<Behaviours>().Find(code) is not { } airline)
        {
            return SupplierError.UnknownAirline.ToResult();
        }

        Activity.Current?.SetTag("airlinesim.airline", code);

        if (airline.Knobs.Outage)
        {
            Telemetry.Fault(code, "outage");
            return SupplierError.ServiceUnavailable.ToResult();
        }

        TimeSpan delay = airline.Latency();
        if (delay > TimeSpan.Zero)
        {
            // A span of its own, so a trace shows the wait was the sim's doing and not the network's.
            using (Activity? activity = Telemetry.Source.StartActivity("airlinesim.injected_latency"))
            {
                activity?.SetTag("airlinesim.airline", code);
                activity?.SetTag("airlinesim.latency_ms", delay.TotalMilliseconds);
                await Task.Delay(delay, http.RequestAborted);
            }

            Telemetry.InjectedLatency.Record(delay.TotalMilliseconds, new KeyValuePair<string, object?>("airline", code));
        }

        return await next(context);
    }
}
