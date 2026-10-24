using Viatica.PspSim.Behaviour;

namespace Viatica.PspSim.Api;

/// <summary>
/// Runtime control of the provider's behaviour, for system tests that stage failures. Unauthenticated on purpose:
/// the sim only ever runs inside a test stack, and this API must never be exposed outside one.
/// </summary>
/// <remarks>
/// Knobs live in process memory. Run one instance of the sim; a second one would keep its own knobs and its own
/// ledger.
/// </remarks>
internal static class AdminApi
{
    private const string LogCategory = "Viatica.PspSim.Admin";

    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder admin = app.MapGroup("/admin");

        admin.MapGet("/knobs", static (ProviderState state) =>
            TypedResults.Json(new KnobsView(state.Knobs, Knobs.Defaults), AdminJson.Default.KnobsView));

        admin.MapPatch("/knobs", static async (HttpRequest request, ProviderState state, ILoggerFactory loggers) =>
        {
            KnobsPatch? patch = await request.ReadFromJsonAsync(AdminJson.Default.KnobsPatch, request.HttpContext.RequestAborted);
            if (patch is null)
            {
                return Ledger.ProviderError.Invalid("parameter_missing", "A body of knobs to change is required.").ToResult();
            }

            Knobs knobs = patch.ApplyTo(state.Knobs);
            if (knobs.Problem() is { } problem)
            {
                return Ledger.ProviderError.Invalid("parameter_invalid", problem).ToResult();
            }

            state.Set(knobs);
            Log.KnobsChanged(loggers.CreateLogger(LogCategory), knobs.ToString());
            return TypedResults.Json(new KnobsView(knobs, Knobs.Defaults), AdminJson.Default.KnobsView);
        });

        admin.MapPost("/knobs/reset", static (ProviderState state, ILoggerFactory loggers) =>
        {
            state.Reset();
            Log.KnobsReset(loggers.CreateLogger(LogCategory));
            return TypedResults.NoContent();
        });

        // The test card numbers are public and fixed, so the sim can list them for whoever is writing a test.
        admin.MapGet("/cards", static () =>
            TypedResults.Json(
                new ListView<TestCardView>("list", [.. Cards.All.Select(static c => new TestCardView(c.Number, c.Brand, c.Behaviour.ToString()))]),
                SimJson.Default.ListViewTestCardView));
    }
}
