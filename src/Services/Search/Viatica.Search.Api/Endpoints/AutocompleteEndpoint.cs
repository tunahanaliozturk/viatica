using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Relevox;
using Viatica.Kernel;
using Viatica.Kernel.Web;
using Viatica.Search.Application.Places;

namespace Viatica.Search.Api.Endpoints;

/// <summary>Suggestions are a short list for a type-ahead box, so there is no <c>nextLink</c>.</summary>
public sealed record AutocompleteResponse(IReadOnlyList<Place> Value);

internal static class AutocompleteEndpoint
{
    private static readonly FrozenSet<string> Parameters = FrozenSet.ToFrozenSet(
        ["q", "types", "api-version", Paging.Top], StringComparer.Ordinal);

    public static void MapAutocomplete(this IEndpointRouteBuilder app) =>
        app.MapGet("/autocomplete", async Task<Ok<AutocompleteResponse>> (HttpContext http, ISender sender, CancellationToken cancellationToken) =>
        {
            var q = new QueryReader(http.Request.Query, Parameters);
            string text = q.Text("q") ?? "";
            IReadOnlyList<string> types = q.List("types");
            HashSet<PlaceKind> kinds = [];
            foreach (string type in types)
            {
                if (Enum.TryParse(type, ignoreCase: true, out PlaceKind kind) && Enum.IsDefined(kind))
                {
                    kinds.Add(kind);
                }
                else
                {
                    q.Problem("types", "InvalidValue", "types is a comma separated list of city, airport, property.");
                    break;
                }
            }

            q.ThrowIfInvalid();
            int top = http.BindPageRequest(Autocomplete.DefaultTop, Autocomplete.MaxTop).Top;
            if (types.Count == 0)
            {
                kinds = [.. Enum.GetValues<PlaceKind>()];
            }

            IReadOnlyList<Place> places = await sender.Send(new Autocomplete(text, kinds, top), cancellationToken);
            return TypedResults.Ok(new AutocompleteResponse(places));
        })
        .WithName("Autocomplete")
        .WithSummary("Cities, airports and properties that match what has been typed.");
}
