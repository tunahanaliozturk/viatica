using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using Viatica.Kernel.Messaging;
using Viatica.Kernel.Validation;
using Viatica.Kernel.Web;
using Viatica.Stays.Api;
using Viatica.Stays.Api.Endpoints;
using Viatica.Stays.Application;
using Viatica.Stays.Infrastructure;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Before AddViaticaApi on purpose: the kernel's handler turns every exception into a response, so this one only
// gets to see a database failure if it is asked first.
builder.Services.AddExceptionHandler<StaysExceptionHandler>();
builder.AddViaticaApi();
builder.AddViaticaAuth("stays-api");
builder.Services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, StaysJsonContext.Default));

// Deny by default: an endpoint that names no policy is still closed, and the public ones say so with AllowAnonymous.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(PropertyEndpoints.PartnerOrAdmin, static policy => policy.RequireRole(Roles.Partner, Roles.Admin));

// The quote key is Stays' alone. It is read here, not in the library, so a missing or short key stops the host at
// start-up and not at the first quote.
byte[] quoteKey = Convert.FromBase64String(
    builder.Configuration["Stays:QuoteKey"] ?? throw new InvalidOperationException("Stays:QuoteKey is not set."));
builder.Services.AddStaysApplication(quoteKey);
builder.Services.AddStaysInfrastructure(builder.Configuration);
builder.Services.AddRelevox(static options => options.AddOpenBehavior(typeof(ValidationBehavior<,>)));

// Repeatable requests are kept in Valkey, so a retry that lands on another instance still finds its answer. The
// multiplexer connects without insisting that Valkey is up yet, and the host starts either way.
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    ConfigurationOptions options = ConfigurationOptions.Parse(
        sp.GetRequiredService<IConfiguration>().GetConnectionString("Valkey")
        ?? throw new InvalidOperationException("ConnectionStrings:Valkey is not set."));
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddValkeyRepeatabilityStore();

builder.Services.AddHealthChecks().AddDbContextCheck<StaysDbContext>(tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(static resource => resource.AddService(StaysTelemetry.Name))
    .WithTracing(static tracing => tracing
        .AddSource(StaysTelemetry.Name, MessagingTelemetry.Name)
        .AddAspNetCoreInstrumentation())
    .WithMetrics(static metrics => metrics
        .AddMeter(StaysTelemetry.Name, MessagingTelemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation());

// Export only where a collector is configured, so a test run or a laptop without one is not full of retries.
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

WebApplication app = builder.Build();

// One-shot schema setup, run by compose before the service starts and never on startup: two instances starting
// together must not race to alter the schema.
if (args is ["migrate"])
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<StaysDbContext>().Database.MigrateAsync();
    return 0;
}

app.UseViaticaApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false }).WithoutApiVersion().AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static check => check.Tags.Contains("ready") }).WithoutApiVersion().AllowAnonymous();

app.MapPropertyEndpoints();
app.MapRoomTypeEndpoints();
app.MapNightAndQuoteEndpoints();

await app.RunAsync();
return 0;

/// <summary>The host, visible to the integration tests that start it in process.</summary>
public partial class Program;
