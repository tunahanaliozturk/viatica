using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Kernel.Querying;
using Viatica.Kernel.Validation;
using Viatica.Kernel.Web;

namespace Viatica.Kernel.UnitTests;

/// <summary>
/// A minimal service host wired exactly as a real one is (<c>AddViaticaApi</c>, <c>UseViaticaApi</c>, Relevox
/// with the validation behaviour, an authentication scheme) on an in-process TestServer.
/// </summary>
public sealed class TestApi : IAsyncLifetime
{
    public const string Version = "2026-11-01";
    public const string UserHeader = "X-Test-User";

    private WebApplication? app;

    public HttpClient Client { get; private set; } = null!;

    public Counters Counters { get; } = new();

    public IServiceProvider Services => app!.Services;

    public async ValueTask InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration["Viatica:Api:SkipTokenKey"] = Keys.SkipTokenBase64;
        builder.Configuration["Viatica:Api:SupportedApiVersions:0"] = Version;

        builder.AddViaticaApi();
        builder.Services.AddSingleton(Counters);
        builder.Services.AddValidator<CreateTrip, CreateTripValidator>();
        builder.Services.AddRelevox(options => options.AddOpenBehavior(typeof(ValidationBehavior<,>)));
        builder.Services.AddAuthentication(TestAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(TestAuthentication.SchemeName, null);
        builder.Services.AddAuthorization();

        app = builder.Build();
        app.UseViaticaApi();
        Map(app, Counters);

        await app.StartAsync();
        Client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    private static void Map(WebApplication app, Counters counters)
    {
        app.MapGet("/health", static () => "ok").WithoutApiVersion();
        app.MapGet("/me", static (ClaimsPrincipal user) => user.Identity!.Name).RequireAuthorization();

        app.MapGet("/stays", static (HttpContext http) =>
        {
            CollectionQuery<StayRow> query = http.BindCollectionQuery(Stays.Fields);
            return http.Page(query.ToPage([.. query.ApplyTo(Stays.Rows.AsQueryable())], static s => new StaySummary(s.Id, s.City, Money.Eur(s.FromPrice))));
        });

        app.MapGet("/boom", static IResult () => throw new InvalidOperationException("Password=hunter2 on db-primary"));
        app.MapGet("/sold-out", static IResult () => throw ApiErrorException.Conflict("SoldOut", "No rooms left for those nights."));
        app.MapPost("/echo", static (Echo echo) => echo);

        app.MapPost("/trips", static async (CreateTrip trip, ISender sender, CancellationToken cancellationToken) =>
            TypedResults.Ok(await sender.Send(trip, cancellationToken)));

        app.MapGet("/rooms/{id:int}", static (int id, HttpContext http) =>
        {
            Room room = Rooms.Get(id);
            http.Response.SetETag(room.Version);
            return room;
        });

        app.MapPut("/rooms/{id:int}", static (int id, RoomUpdate update, HttpContext http) =>
        {
            // What a command handler does with the version the endpoint hands it.
            Room current = Rooms.Get(id);
            if (http.IfMatchVersion() is { } expected && expected != current.Version)
            {
                throw ApiErrorException.PreconditionFailed();
            }

            Room updated = Rooms.Put(id, current with { Name = update.Name, Version = current.Version + 1 });
            http.Response.SetETag(updated.Version);
            return updated;
        }).RequireIfMatch();

        app.MapPost("/refunds", static (RefundRequest refund, Counters counters) =>
        {
            int number = Interlocked.Increment(ref counters.Refunds);
            return TypedResults.Created($"/refunds/{number}", new Refund(number, refund.Amount));
        }).WithRepeatability();

        app.MapPost("/refunds/declined", static (Counters counters) =>
        {
            Interlocked.Increment(ref counters.Declined);
            throw ApiErrorException.Conflict("RefundWindowClosed", "The refund window has closed.");
        }).WithRepeatability();

        app.MapPost("/refunds/flaky", static (Counters counters) =>
        {
            return Interlocked.Increment(ref counters.Flaky) == 1
                ? throw new TimeoutException("payment provider timed out")
                : TypedResults.Ok(counters.Flaky);
        }).WithRepeatability();

        app.MapPost("/refunds/slow", static async (Counters counters) =>
        {
            Interlocked.Increment(ref counters.Slow);
            await counters.SlowGate.Task;
            return TypedResults.Ok("done");
        }).WithRepeatability();

        app.MapPost("/checkouts", static (HttpContext http) =>
            http.Accepted(new Operation("op-1", OperationStatus.NotStarted), "/operations/op-1"));

        app.MapGet("/operations/{id}", static (string id) =>
            new Operation(id, OperationStatus.Failed)
            {
                Error = new ApiError("HoldExpired", "The hold on the room expired.") { InnerError = new InnerError("StayHoldExpired") },
            });
    }
}

public sealed class Counters
{
#pragma warning disable CA1051 // Interlocked needs a field to take by reference.
    public int Refunds;
    public int Declined;
    public int Flaky;
    public int Slow;
#pragma warning restore CA1051

    public TaskCompletionSource SlowGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record StaySummary(int Id, string City, Money FromPrice);

public sealed record Echo(string Name, int Count);

public sealed record Room(int Id, string Name, ulong Version);

public sealed record RoomUpdate(string Name);

public sealed record RefundRequest(Money Amount);

public sealed record Refund(int Number, Money Amount);

public sealed record TripCreated(string Name);

public sealed record CreateTrip(string Name, int Guests, DateOnly CheckIn, DateOnly CheckOut) : IRequest<TripCreated>;

public sealed class CreateTripHandler : IRequestHandler<CreateTrip, TripCreated>
{
    public Task<TripCreated> Handle(CreateTrip request, CancellationToken cancellationToken) =>
        Task.FromResult(new TripCreated(request.Name));
}

public sealed class CreateTripValidator : AbstractValidator<CreateTrip>
{
    public CreateTripValidator()
    {
        RuleFor(static t => !string.IsNullOrWhiteSpace(t.Name), "name is required.", nameof(CreateTrip.Name))
            .WithErrorCode("NameRequired");
        RuleFor(static t => t.Guests is >= 1 and <= 9, "guests must be between 1 and 9.", nameof(CreateTrip.Guests))
            .WithErrorCode("GuestsOutOfRange");
        RuleFor(static t => t.CheckOut > t.CheckIn, "checkOut must be after checkIn.", "Items[0].CheckOut")
            .WithErrorCode("CheckOutBeforeCheckIn");
    }
}

internal static class Rooms
{
    private static readonly ConcurrentDictionary<int, Room> All = new();

    public static Room Get(int id) =>
        All.GetOrAdd(id, static id => new Room(id, "Double", 7)) is { } room ? room : throw ApiErrorException.NotFound();

    public static Room Put(int id, Room room) => All[id] = room;
}

/// <summary>Signs in whoever the <c>X-Test-User</c> header names; no header, no identity.</summary>
internal sealed class TestAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? user = Request.Headers[TestApi.UserHeader];
        if (string.IsNullOrEmpty(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim("sub", user), new Claim(ClaimTypes.Name, user)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
