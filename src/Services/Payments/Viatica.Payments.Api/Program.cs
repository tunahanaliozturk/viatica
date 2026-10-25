using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Relevox;
using Viatica.Kernel.Messaging;
using Viatica.Kernel.Validation;
using Viatica.Kernel.Web;
using Viatica.Payments.Api;
using Viatica.Payments.Application;
using Viatica.Payments.Application.Features.TokeniseCard;
using Viatica.Payments.Infrastructure;
using Viatica.Payments.Infrastructure.Persistence;

// "healthcheck" asks this same process image whether the service is ready, for compose: the runtime image has no
// curl or wget, and starting the whole host just to probe another instance would be a long way round.
if (args is ["healthcheck"])
{
    return await HealthProbe.RunAsync();
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddViaticaApi();
builder.AddViaticaAuth("payments-api");
builder.Services.AddPaymentsInfrastructure(builder.Configuration);

builder.Services.AddValidator<TokeniseCardCommand, TokeniseCardValidator>();
builder.Services.AddRelevox(options => options.AddOpenBehavior(typeof(ValidationBehavior<,>)));
builder.Services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, PaymentsJsonContext.Default));
builder.Services.AddOpenApi();

// Live says the process runs. Ready says it can do its job: the database holds the payments. The provider is not part
// of readiness on purpose. A provider outage is handled per call by the circuit breaker, and a service that reports
// itself unready for it would be taken out of rotation while it can still answer every request that needs no provider.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PaymentsDbContext>(tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(static resource => resource.AddService(PaymentsTelemetry.Name))
    .WithTracing(static tracing => tracing
        .AddSource(PaymentsTelemetry.Name, MessagingTelemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(static metrics => metrics
        .AddMeter(PaymentsTelemetry.Name, MessagingTelemetry.Name)
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
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
    return 0;
}

app.UseViaticaApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false }).WithoutApiVersion();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static check => check.Tags.Contains("ready") }).WithoutApiVersion();
app.MapOpenApi().WithoutApiVersion();
app.MapPaymentEndpoints();

await app.RunAsync();
return 0;

/// <summary>Lets the integration tests host the service with <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
