using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Relevox;
using Viatica.Flights.Api;
using Viatica.Flights.Application;
using Viatica.Flights.Application.Features.Search;
using Viatica.Flights.Infrastructure;
using Viatica.Flights.Infrastructure.Persistence;
using Viatica.Kernel.Messaging;
using Viatica.Kernel.Validation;
using Viatica.Kernel.Web;

// "healthcheck" asks this same process image whether the service is ready, for compose: the runtime image has no
// curl or wget, and starting the whole host just to probe another instance would be a long way round.
if (args is ["healthcheck"])
{
    return await HealthProbe.RunAsync();
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddViaticaApi();
builder.AddViaticaAuth("flights-api");
builder.Services.AddFlightsInfrastructure(builder.Configuration);

builder.Services.AddValidator<SearchFlightsCommand, SearchFlightsValidator>();
builder.Services.AddRelevox(options => options.AddOpenBehavior(typeof(ValidationBehavior<,>)));
builder.Services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, FlightsJsonContext.Default));
builder.Services.AddOpenApi();

// Live says the process runs. Ready says it can do its job: the database for orders, Valkey for offers.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<FlightsDbContext>(tags: ["ready"])
    .AddCheck<ValkeyHealthCheck>("valkey", tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(static resource => resource.AddService(FlightsTelemetry.Name))
    .WithTracing(static tracing => tracing
        .AddSource(FlightsTelemetry.Name, MessagingTelemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(static metrics => metrics
        .AddMeter(FlightsTelemetry.Name, MessagingTelemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation());

// Export only where a collector is configured, so a laptop without one is not full of retries.
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

WebApplication app = builder.Build();

// One-shot schema setup, run by compose before the service starts and never on startup.
if (args is ["migrate"])
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<FlightsDbContext>().Database.MigrateAsync();
    return 0;
}

app.UseViaticaApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false }).WithoutApiVersion();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static check => check.Tags.Contains("ready") }).WithoutApiVersion();
app.MapOpenApi().WithoutApiVersion();
app.MapFlightEndpoints();

await app.RunAsync();
return 0;

/// <summary>Lets the integration tests host the service with <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
