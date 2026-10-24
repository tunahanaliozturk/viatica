using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Viatica.PspSim;
using Viatica.PspSim.Api;
using Viatica.PspSim.Behaviour;
using Viatica.PspSim.Ledger;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Read lazily from configuration, so a test host can set its own seed before anything is built.
const int DefaultSeed = 20261025;
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton(static services =>
    new ProviderState(services.GetRequiredService<IConfiguration>().GetValue("PspSim:Seed", DefaultSeed)));
builder.Services.AddSingleton<ProviderLedger>();

// Nothing to check but the process itself: the ledger is in memory.
builder.Services.AddHealthChecks();

// The provider speaks snake_case. The options' naming policy, not the context's, decides what request binding reads,
// so it is set here as well as on SimJson. The admin API reads its bodies through AdminJson explicitly.
builder.Services.ConfigureHttpJsonOptions(static options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
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

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
app.MapProviderApi();
app.MapAdminApi();

await app.RunAsync();
