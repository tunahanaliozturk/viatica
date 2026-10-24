using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Moongazing.OrionGuard.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Relevox;
using Viatica.Kernel.Validation;
using Viatica.Kernel.Web;
using Viatica.Search.Api;
using Viatica.Search.Api.Endpoints;
using Viatica.Search.Application;
using Viatica.Search.Application.Cars;
using Viatica.Search.Application.Places;
using Viatica.Search.Application.Stays;
using Viatica.Search.Infrastructure;
using Viatica.Search.Infrastructure.Indexing;
using Viatica.Search.Infrastructure.Messaging;

// The container's health check is this same binary asked one question, because the runtime image has no curl and
// starting the whole host to answer it would cost seconds.
if (args is ["healthcheck"])
{
    return await HealthProbe.RunAsync();
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// `migrate` and `rebuild` build the same host to reuse its configuration and services, then run once and exit; they
// must not start a consumer. Anything else serves traffic and reads Kafka (a test host passes arguments of its own).
bool serving = args is not (["migrate"] or ["rebuild"]);

builder.AddViaticaApi();
builder.AddViaticaAuth("search-api");
builder.Services.AddSearchInfrastructure(builder.Configuration, runConsumers: serving);

builder.Services.AddValidator<SearchStays, SearchStaysValidator>();
builder.Services.AddValidator<SearchCars, SearchCarsValidator>();
builder.Services.AddValidator<Autocomplete, AutocompleteValidator>();
builder.Services.AddRelevox(options => options.AddOpenBehavior(typeof(ValidationBehavior<,>)));

builder.Services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, SearchJsonContext.Default));
builder.Services.AddOpenApi();

builder.Services.AddHealthChecks().AddCheck<IndicesReadyCheck>("indices", tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(static resource => resource.AddService(SearchTelemetry.Name))
    .WithTracing(static tracing => tracing.AddSource(SearchTelemetry.Name).AddAspNetCoreInstrumentation())
    .WithMetrics(static metrics => metrics
        .AddMeter(SearchTelemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        // The default buckets start at 5 s, which would put every healthy lag sample in the first one.
        .AddView("viatica.search.projection.lag", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 300],
        }));

// Export only where a collector is configured, so a test run or a laptop without one is not full of retries.
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

WebApplication app = builder.Build();

// One-shot index setup and rebuild, run by compose or an operator, never on startup (the same rule as EF Core
// migrations in the other services).
switch (args)
{
    case ["migrate"]:
        await app.Services.GetRequiredService<IndexAdmin>().EnsureAsync(CancellationToken.None);
        return 0;

    case ["rebuild"]:
        RebuildReport report = await app.Services.GetRequiredService<RebuildRunner>().RunAsync(CancellationToken.None);
        string removed = string.Join(", ", report.RemovedIndices);
        Log.Rebuilt(app.Logger, report.EventsRead, report.NewIndices[IndexKind.Stays], report.NewIndices[IndexKind.Cars], report.Elapsed, removed);
        return 0;
}

app.UseViaticaApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false }).WithoutApiVersion();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static check => check.Tags.Contains("ready") }).WithoutApiVersion();
app.MapOpenApi().WithoutApiVersion();

// Anonymous on purpose: travellers search before they sign in. Nothing here reads a token, so there is no policy
// to require; the gateway applies the per-IP rate limits that keep this open.
app.MapStays();
app.MapCars();
app.MapAutocomplete();

await app.RunAsync();
return 0;

/// <summary>The host, for <c>WebApplicationFactory</c>.</summary>
public partial class Program;
