using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Relevox;
using Viatica.Booking.Api;
using Viatica.Booking.Api.Endpoints;
using Viatica.Booking.Application;
using Viatica.Booking.Infrastructure;
using Viatica.Booking.Infrastructure.Persistence;
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

// Registered before the kernel's handler on purpose: handlers run in registration order, and the kernel's answers
// every exception it does not know with a 500.
builder.Services.AddExceptionHandler<DatabaseExceptionHandler>();
builder.AddViaticaApi();
builder.AddViaticaAuth("booking-api");
builder.Services.AddBookingInfrastructure(builder.Configuration);

builder.Services.AddRelevox(static options => options.AddOpenBehavior(typeof(ValidationBehavior<,>)));
builder.Services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, BookingJsonContext.Default));
builder.Services.AddOpenApi();

builder.Services.AddHealthChecks().AddDbContextCheck<BookingDbContext>(tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(static resource => resource.AddService(BookingTelemetry.Name))
    .WithTracing(static tracing => tracing
        .AddSource(BookingTelemetry.Name, MessagingTelemetry.Name)
        .AddAspNetCoreInstrumentation())
    .WithMetrics(static metrics => metrics
        .AddMeter(BookingTelemetry.Name, MessagingTelemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation());

// Export only where a collector is configured, so a laptop without one is not full of retries.
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

WebApplication app = builder.Build();

// One-shot schema setup, run by compose before the service starts and never on startup: two instances racing to
// migrate one database is a failure waiting for a deploy.
if (args is ["migrate"])
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<BookingDbContext>().Database.MigrateAsync();
    return 0;
}

app.UseViaticaApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false }).WithoutApiVersion();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static check => check.Tags.Contains("ready") }).WithoutApiVersion();
app.MapOpenApi().WithoutApiVersion();

app.MapTripEndpoints();
app.MapCheckoutEndpoints();
app.MapCancellationEndpoints();
app.MapPartnerEndpoints();

await app.RunAsync();
return 0;

/// <summary>Lets the integration tests host the real service with <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
