using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Npgsql;
using Viatica.Kernel;
using Viatica.Payments.Application.Provider;
using Viatica.Payments.Domain.Cards;
using Viatica.Payments.Infrastructure.Persistence;
using Viatica.Payments.Infrastructure.Provider;
using Viatica.Testing;

namespace Viatica.Payments.IntegrationTests;

/// <summary>What a test class changes about the way its Payments host is set up.</summary>
internal sealed record HostSettings
{
    /// <summary>Longest any provider call may take. A test about a timeout makes it short, so the wait for it is short.</summary>
    public string RequestTimeout { get; init; } = "00:00:01";

    /// <summary>
    /// High by default, so the breaker never opens under a test that switches the provider off and on: its window is 30
    /// seconds long and spans tests. The one test class about the breaker lowers it.
    /// </summary>
    public int BreakerMinimumThroughput { get; init; } = 1000;

    public string BreakDuration { get; init; } = "00:00:15";

    /// <summary>Consumer attempts per message, with delays short enough that a retry shows within a test.</summary>
    public int ConsumerAttempts { get; init; } = 8;

    public string ConsumerMaxRetryDelay { get; init; } = "00:00:01";
}

/// <summary>
/// The real Payments service: its own Postgres database (migrated by the service migration), Kafka topics of its own,
/// and the provider reached over HTTP. Every request that leaves for the provider is recorded, with its body.
/// </summary>
internal sealed class PaymentsHost : WebApplicationFactory<Program>
{
    public const string ApiVersion = "2026-11-01";
    public const string Audience = "payments-api";

    private readonly Dictionary<string, string?> settings;
    private readonly SimHost sim;

    private PaymentsHost(Dictionary<string, string?> settings, SimHost sim, WireLog wire, CapturedLogs logs, ProviderFaults faults)
    {
        this.settings = settings;
        this.sim = sim;
        Wire = wire;
        Logs = logs;
        Faults = faults;
    }

    /// <summary>Every request that actually reached the provider, so a test can prove a call was or was not made.</summary>
    public WireLog Wire { get; }

    public CapturedLogs Logs { get; }

    /// <summary>Faults injected between the service and the provider: a crash after the provider has answered.</summary>
    public ProviderFaults Faults { get; }

    public string BookingTopic => settings["Kafka:BookingTopic"]!;

    public string PaymentsTopic => settings["Kafka:PaymentsTopic"]!;

    public string PostgresConnection => settings["ConnectionStrings:Payments"]!;

    public static async Task<PaymentsHost> StartAsync(PostgresFixture postgres, KafkaFixture kafka, SimHost sim, HostSettings? host = null)
    {
        host ??= new HostSettings();
        string connection = await postgres.CreateDatabaseAsync("payments");

        // The schema is the service migration, applied before the host starts so its consumer finds the tables.
        await using (var db = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(connection).Options))
        {
            await db.Database.MigrateAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Payments"] = connection,
            ["Kafka:BootstrapServers"] = kafka.BootstrapServers,
            ["Kafka:BookingTopic"] = await kafka.CreateTopicAsync("booking"),
            ["Kafka:PaymentsTopic"] = await kafka.CreateTopicAsync("payments"),
            ["Viatica:Api:SupportedApiVersions:0"] = ApiVersion,
            ["Viatica:Api:SkipTokenKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Viatica:Auth:TestSigningKey"] = TestTokens.SigningKeyBase64,
            ["Payments:Provider:BaseUrl"] = "http://psp.test/",
            ["Payments:Provider:RequestTimeout"] = host.RequestTimeout,
            ["Payments:Provider:CircuitBreaker:MinimumThroughput"] = host.BreakerMinimumThroughput.ToString(CultureInfo.InvariantCulture),
            ["Payments:Provider:CircuitBreaker:BreakDuration"] = host.BreakDuration,
            ["Payments:Consumer:MaxAttempts"] = host.ConsumerAttempts.ToString(CultureInfo.InvariantCulture),
            ["Payments:Consumer:RetryDelay"] = "00:00:00.100",
            ["Payments:Consumer:MaxRetryDelay"] = host.ConsumerMaxRetryDelay,
        };

        var payments = new PaymentsHost(settings, sim, new WireLog(), new CapturedLogs(), new ProviderFaults());
        _ = payments.Services; // starts the host, and with it the outbox relay and the consumer
        return payments;
    }

    public async Task<T> InDbAsync<T>(Func<PaymentsDbContext, Task<T>> work)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        // Each host has a database of its own, and so a pool of its own that nothing would otherwise close. Left open they
        // add up past the server's 100 connections over a run (the same as StaysHost).
        using var connection = new NpgsqlConnection(PostgresConnection);
        NpgsqlConnection.ClearPool(connection);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach ((string key, string? value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(Logs);
            // Everything, from every category: the log test must see what a debugging session would see.
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter<CapturedLogs>(null, LogLevel.Trace);
        });

        builder.ConfigureTestServices(services =>
        {
            services.Configure<HttpClientFactoryOptions>(PspClient.Name, options =>
                options.HttpMessageHandlerBuilderActions.Add(handler =>
                    handler.PrimaryHandler = new CountingHandler(Wire, sim.CreateProviderHandler())));

            // The gateway stays as registered; this sits in front of it and passes everything through until a test arms it.
            services.RemoveAll<IPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(sp => new FaultInjectingProvider(sp.GetRequiredService<PspGateway>(), Faults));
        });
    }
}

/// <summary>One request that reached the provider.</summary>
internal sealed record WireEntry(HttpMethod Method, string Path, string Query, string? IdempotencyKey, string Body);

internal sealed class WireLog
{
    private readonly List<WireEntry> entries = [];

    public IReadOnlyList<WireEntry> Entries
    {
        get
        {
            lock (entries)
            {
                return [.. entries];
            }
        }
    }

    public void Add(WireEntry entry)
    {
        lock (entries)
        {
            entries.Add(entry);
        }
    }

    /// <summary>Requests whose path ends with <paramref name="pathEndsWith"/>, e.g. <c>/capture</c>.</summary>
    public int Count(HttpMethod method, string pathEndsWith) =>
        Entries.Count(e => e.Method == method && e.Path.EndsWith(pathEndsWith, StringComparison.Ordinal));

    public IReadOnlyList<WireEntry> Posts(string pathEndsWith) =>
        [.. Entries.Where(e => e.Method == HttpMethod.Post && e.Path.EndsWith(pathEndsWith, StringComparison.Ordinal))];

    public int Lookups => Entries.Count(e => e.Method == HttpMethod.Get && e.Path.EndsWith("/v1/operations", StringComparison.Ordinal));

    public int Total => Entries.Count;
}

/// <summary>Records what goes out, then hands it to the sim. It sits innermost, below the resilience pipeline, so a call the breaker refuses is not recorded: it never left.</summary>
internal sealed class CountingHandler(WireLog wire, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        wire.Add(new WireEntry(
            request.Method,
            request.RequestUri!.AbsolutePath,
            request.RequestUri.Query,
            request.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? values) ? values.Single() : null,
            body));

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Faults between the service and the provider that no knob of the provider can make.</summary>
internal sealed class ProviderFaults
{
    private int crashesAfterCapture;

    /// <summary>The next capture reaches the provider and gets its answer; then the process "dies" before it can record it.</summary>
    public void CrashAfterNextCapture() => Interlocked.Increment(ref crashesAfterCapture);

    internal bool TakeCrashAfterCapture()
    {
        while (true)
        {
            int current = Volatile.Read(ref crashesAfterCapture);
            if (current == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref crashesAfterCapture, current - 1, current) == current)
            {
                return true;
            }
        }
    }
}

/// <summary>A provider that is the real one, except when a test arms a fault.</summary>
internal sealed class FaultInjectingProvider(IPaymentProvider inner, ProviderFaults faults) : IPaymentProvider
{
    public Task<TokeniseOutcome> TokeniseAsync(CardDetails card, string idempotencyKey, CancellationToken cancellationToken) =>
        inner.TokeniseAsync(card, idempotencyKey, cancellationToken);

    public Task<ProviderResult> AuthoriseAsync(
        string providerToken, Money amount, Guid tripId, Guid paymentId, string idempotencyKey, CancellationToken cancellationToken) =>
        inner.AuthoriseAsync(providerToken, amount, tripId, paymentId, idempotencyKey, cancellationToken);

    public async Task<ProviderResult> CaptureAsync(string intentId, Money amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        ProviderResult result = await inner.CaptureAsync(intentId, amount, idempotencyKey, cancellationToken);
        if (faults.TakeCrashAfterCapture())
        {
            // The provider has captured and answered. This is the moment a process can die before its commit.
            throw new InvalidOperationException("Simulated crash after the provider answered and before the commit.");
        }

        return result;
    }

    public Task<ProviderResult> VoidAsync(string intentId, string idempotencyKey, CancellationToken cancellationToken) =>
        inner.VoidAsync(intentId, idempotencyKey, cancellationToken);

    public Task<ProviderResult> RefundAsync(
        string intentId, Money amount, Guid tripId, Guid refundId, string idempotencyKey, CancellationToken cancellationToken) =>
        inner.RefundAsync(intentId, amount, tripId, refundId, idempotencyKey, cancellationToken);

    public Task<LookupResult> LookUpAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        inner.LookUpAsync(idempotencyKey, cancellationToken);
}
