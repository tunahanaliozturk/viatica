using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Search.Application.Projection;

namespace Viatica.Search.Application.Places;

public enum PlaceKind
{
    City,
    Airport,
    Property,
}

/// <summary>One suggestion. <see cref="Code"/> is the IATA code of an airport; <see cref="Id"/> is a property's id.</summary>
public sealed record Place(PlaceKind Kind, string Name, string? Detail, string? Code, Guid? Id, GeoLocation? Location);

/// <summary>Suggestions for what a traveller has typed so far, across the kinds asked for.</summary>
public sealed record Autocomplete(string Text, IReadOnlySet<PlaceKind> Kinds, int Top) : IRequest<IReadOnlyList<Place>>
{
    public const int MinTextLength = 2;
    public const int MaxTextLength = 60;
    public const int DefaultTop = 8;
    public const int MaxTop = 20;
}

/// <summary>The cities and properties of the search index, matched by what has been typed of their names.</summary>
public interface IPlaceIndex
{
    Task<IReadOnlyList<Place>> CitiesAsync(string text, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Place>> PropertiesAsync(string text, int limit, CancellationToken cancellationToken);
}

/// <summary>The airports the platform knows, held in memory.</summary>
public interface IAirportDirectory
{
    IReadOnlyList<Place> Find(string text, int limit);
}

public sealed class AutocompleteValidator : AbstractValidator<Autocomplete>
{
    public AutocompleteValidator()
    {
        RuleFor(static r => r.Text.Trim().Length >= Autocomplete.MinTextLength,
                $"q needs at least {Autocomplete.MinTextLength} characters.", "q")
            .WithErrorCode("QueryTooShort");
        RuleFor(static r => r.Text.Length <= Autocomplete.MaxTextLength,
                $"q can be at most {Autocomplete.MaxTextLength} characters.", "q")
            .WithErrorCode("QueryTooLong");
        RuleFor(static r => r.Kinds.Count > 0, "types names at least one of city, airport, property.", "types")
            .WithErrorCode("InvalidTypes");
    }
}

public sealed class AutocompleteHandler(IPlaceIndex places, IAirportDirectory airports)
    : IRequestHandler<Autocomplete, IReadOnlyList<Place>>
{
    public async Task<IReadOnlyList<Place>> Handle(Autocomplete request, CancellationToken cancellationToken)
    {
        string text = request.Text.Trim();
        int top = request.Top;

        // Each kind is asked for up to the page size, then the three lists are merged. A city the traveller is
        // typing beats a hotel with the same prefix, and an airport whose code is exactly what was typed beats both.
        Task<IReadOnlyList<Place>> cities = request.Kinds.Contains(PlaceKind.City)
            ? places.CitiesAsync(text, top, cancellationToken)
            : Task.FromResult<IReadOnlyList<Place>>([]);
        Task<IReadOnlyList<Place>> properties = request.Kinds.Contains(PlaceKind.Property)
            ? places.PropertiesAsync(text, top, cancellationToken)
            : Task.FromResult<IReadOnlyList<Place>>([]);
        IReadOnlyList<Place> matchingAirports = request.Kinds.Contains(PlaceKind.Airport) ? airports.Find(text, top) : [];

        IReadOnlyList<Place>[] found = await Task.WhenAll(cities, properties);

        Place[] byCode = [.. matchingAirports.Where(a => string.Equals(a.Code, text, StringComparison.OrdinalIgnoreCase))];
        return
        [
            .. byCode
                .Concat(found[0])
                .Concat(matchingAirports.Except(byCode))
                .Concat(found[1])
                .Take(top),
        ];
    }
}
