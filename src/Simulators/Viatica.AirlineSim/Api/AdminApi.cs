using Viatica.AirlineSim.Behaviour;

namespace Viatica.AirlineSim.Api;

/// <summary>
/// Runtime control of the airlines' behaviour, for system tests that stage failures. Unauthenticated on purpose:
/// the sim only ever runs inside a test stack, and this API must never be exposed outside one.
/// </summary>
/// <remarks>
/// Knobs live in process memory. Run one instance of the sim; a second one would keep its own knobs.
/// </remarks>
internal static class AdminApi
{
    private const string LogCategory = "Viatica.AirlineSim.Admin";

    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder admin = app.MapGroup("/admin");

        admin.MapGet("/airlines", static (Behaviours behaviours) =>
            TypedResults.Json<IReadOnlyList<AirlineView>>([.. behaviours.All.Select(View)], SimJson.Default.IReadOnlyListAirlineView));

        admin.MapPatch("/airlines/{code}/knobs", static (string code, KnobsPatch patch, Behaviours behaviours, ILoggerFactory loggers) =>
        {
            if (behaviours.Find(code) is not { } airline)
            {
                return SupplierError.UnknownAirline.ToResult();
            }

            Knobs knobs = patch.ApplyTo(airline.Knobs);
            if (knobs.Problem() is { } problem)
            {
                return SupplierError.Invalid(problem).ToResult();
            }

            airline.Set(knobs);
            Log.KnobsChanged(loggers.CreateLogger(LogCategory), code, knobs.ToString());
            return TypedResults.Json(View(airline), SimJson.Default.AirlineView);
        });

        admin.MapPost("/knobs/reset", static (Behaviours behaviours, ILoggerFactory loggers) =>
        {
            behaviours.ResetAll();
            Log.KnobsReset(loggers.CreateLogger(LogCategory));
            return TypedResults.NoContent();
        });
    }

    private static AirlineView View(AirlineState state) =>
        new(state.Airline.Code, state.Airline.Name, state.Airline.Hub, state.Airline.Behaviour, state.Knobs, state.Airline.Defaults);
}
