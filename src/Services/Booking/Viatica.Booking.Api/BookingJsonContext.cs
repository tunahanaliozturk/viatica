using System.Text.Json;
using System.Text.Json.Serialization;
using Viatica.Booking.Application;
using Viatica.Booking.Application.Features.Partner;
using Viatica.Booking.Application.Features.Trips;
using Viatica.Kernel;
using Viatica.Kernel.Web;

namespace Viatica.Booking.Api;

/// <summary>
/// Source-generated JSON for the request and response bodies, with the repository's settings: camelCase, enums as
/// strings, nulls left out, and a body that misses a required member refused instead of defaulted.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ItemSpec))]
[JsonSerializable(typeof(TravellerDetails))]
[JsonSerializable(typeof(TripView))]
[JsonSerializable(typeof(Operation))]
[JsonSerializable(typeof(PagedResponse<TripSummary>))]
[JsonSerializable(typeof(PagedResponse<PartnerBooking>))]
internal sealed partial class BookingJsonContext : JsonSerializerContext;
