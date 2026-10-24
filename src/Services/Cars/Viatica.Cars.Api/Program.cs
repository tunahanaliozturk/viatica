using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using Viatica.Cars.Api;
using Viatica.Cars.Api.Endpoints;
using Viatica.Cars.Application;
using Viatica.Cars.Infrastructure;
using Viatica.Kernel.Messaging;
using Viatica.Kernel.Validation;
using Viatica.Kernel.Web;

// `healthcheck` is what the container's health check runs: the runtime image has no curl, so the app asks itself.
if (args is ["healthcheck"])
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri("http://localhost:8080/health/ready"));
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (TaskCanceledException)
    {
        return 1;
    }
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Registered before the kernel's handler on purpose: handlers run in registration order, and the kernel's answers
// every exception it does not know with a 500.
builder.Services.AddExceptionHandler<DatabaseExceptionHandler>();
builder.AddViaticaApi();
builder.AddViaticaAuth("cars-api");

builder.Services.AddCarsApplication(Convert.FromBase64String(
    builder.Configuration["Viatica:Quotes:SigningKey"]
    ?? throw new InvalidOperationException("Viatica:Quotes:SigningKey is not set.")));
builder.Services.AddCarsInfrastructure(builder.Configuration);

// Bulk inventory updates accept Repeatability-Request-ID, so the kept responses must be shared by every instance.
builder.Services.AddSingleton<IConnectionMultiplexer>(static sp =>
    ConnectionMultiplexer.Connect(
        sp.GetRequiredService<IConfiguration>().GetConnectionString("Valkey")
        ?? throw new InvalidOperationException("ConnectionStrings:Valkey is not set.")));
builder.Services.AddValkeyRepeatabilityStore();

builder.Services.AddRelevox(static options => options.AddOpenBehavior(typeof(ValidationBehavior<,>)));
builder.Services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, CarsJsonContext.Default));

builder.Services.AddHealthChecks().AddDbContextCheck<CarsDbContext>(tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(static resource => resource.AddService(CarsTelemetry.Name))
    .WithTracing(static tracing => tracing
        .AddSource(CarsTelemetry.Name, MessagingTelemetry.Name)
        .AddAspNetCoreInstrumentation())
    .WithMetrics(static metrics => metrics
        .AddMeter(CarsTelemetry.Name, MessagingTelemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation());

// Export only where a collector is configured, so a test run or a laptop without one is not full of retries.
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

WebApplication app = builder.Build();

// One-shot, run by compose before the service starts and never on startup: two instances racing to migrate one
// database is a failure waiting for a deploy.
if (args is ["migrate"])
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<CarsDbContext>().Database.MigrateAsync();
    return 0;
}

app.UseViaticaApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false }).WithoutApiVersion();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static check => check.Tags.Contains("ready") }).WithoutApiVersion();

app.MapLocationEndpoints();
app.MapCategoryEndpoints();
app.MapInventoryEndpoints();
app.MapPartnerSettingsEndpoints();
app.MapQuoteEndpoints();

await app.RunAsync();
return 0;

/// <summary>Lets the integration tests host the real service.</summary>
public partial class Program;
