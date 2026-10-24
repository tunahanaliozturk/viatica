using System.Text.Json;
using System.Text.Json.Serialization;
using Viatica.Kernel.Web;
using Viatica.Stays.Application.Features.Nights;
using Viatica.Stays.Application.Features.Properties;
using Viatica.Stays.Application.Features.Quotes;
using Viatica.Stays.Application.Features.RoomTypes;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Api;

/// <summary>
/// Every request and response body, in generated code. The options repeat the ones in <c>ViaticaJson</c>, because a
/// generated context carries its own: without them a body missing a required member would bind instead of ending in
/// a 400.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PropertyDetails))]
[JsonSerializable(typeof(PropertyPatch))]
[JsonSerializable(typeof(PropertyView))]
[JsonSerializable(typeof(RoomTypeDetails))]
[JsonSerializable(typeof(RoomTypePatch))]
[JsonSerializable(typeof(RoomTypeView))]
[JsonSerializable(typeof(PagedResponse<PropertySummary>))]
[JsonSerializable(typeof(QuoteRequest))]
[JsonSerializable(typeof(Quote))]
[JsonSerializable(typeof(SetNightsRequest))]
[JsonSerializable(typeof(NightsSet))]
[JsonSerializable(typeof(IReadOnlyList<NightView>))]
internal sealed partial class StaysJsonContext : JsonSerializerContext;

/// <summary>The body of a quote request. The ids are in the route.</summary>
public sealed record QuoteRequest(DateOnly CheckIn, DateOnly CheckOut, int Quantity, int Guests);

/// <summary>The body of a bulk calendar edit. Both dates are included; a missing rate means the room type's base rate.</summary>
public sealed record SetNightsRequest(DateOnly From, DateOnly To, int Allotment, Viatica.Kernel.Money? Rate = null);
