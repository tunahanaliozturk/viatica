using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Viatica.AirlineSim;
using Viatica.AirlineSim.Api;
using Viatica.AirlineSim.Behaviour;
using Viatica.AirlineSim.Network;
using Viatica.AirlineSim.Orders;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Read lazily from configuration, so a test host can set its own seed and database before anything is built.
const int DefaultSeed = 20261024;
builder.Services.AddSingleton(static services => new RouteNetwork(Seed(services)));
builder.Services.AddSingleton(static services => new Behaviours(Seed(services)));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.TryAddSingleton<IBookingReferences, RandomBookingReferences>();

builder.Services.AddDbContextPool<AirlineSimDb>(static (services, options) =>
    options
        .UseNpgsql(
            services.GetRequiredService<IConfiguration>().GetConnectionString("AirlineSim")
            ?? throw new InvalidOperationException("ConnectionStrings:AirlineSim is not set."))
        // A unique violation on the idempotency key is the expected end of two creations racing with one key, and
        // the caller handles it. EF Core would log every one at Error; a failure nobody handles still surfaces as
        // an unhandled exception in the request log.
        .ConfigureWarnings(static warnings => warnings.Log((CoreEventId.SaveChangesFailed, LogLevel.Debug))));

builder.Services.AddHealthChecks().AddDbContextCheck<AirlineSimDb>(tags: ["ready"]);

builder.Services.ConfigureHttpJsonOptions(static options =>
{
    // The airline speaks PascalCase. Matching stays case-insensitive on the way in, like most supplier gateways.
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, SimJson.Default);
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(static resource => resource.AddService(Telemetry.Name))
    .WithTracing(static tracing => tracing.AddSource(Telemetry.Name).AddAspNetCoreInstrumentation())
    .WithMetrics(static metrics => metrics.AddMeter(Telemetry.Name).AddAspNetCoreInstrumentation().AddRuntimeInstrumentation());

// Export only where a collector is configured, so a test run or a laptop without one is not full of retries.
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

WebApplication app = builder.Build();

// One-shot schema setup, run by compose before the sim starts, never on startup. EnsureCreated rather than
// migrations: the sim's data is disposable test state, so a schema change means dropping the database, and a
// migration history would be ceremony with nothing to preserve.
if (args is ["migrate"])
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AirlineSimDb>().Database.EnsureCreatedAsync();
    Log.SchemaReady(app.Logger);
    return;
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static check => check.Tags.Contains("ready") });
app.MapSupplierApi();
app.MapAdminApi();

await app.RunAsync();

static int Seed(IServiceProvider services) =>
    services.GetRequiredService<IConfiguration>().GetValue("AirlineSim:Seed", DefaultSeed);
