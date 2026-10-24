using System.Text.Json;
using System.Text.Json.Serialization;
using Viatica.Cars.Api.Endpoints;
using Viatica.Cars.Application;
using Viatica.Cars.Application.Features.GetInventory;
using Viatica.Cars.Application.Features.GetQuote;
using Viatica.Cars.Application.Features.PartnerSettings;
using Viatica.Cars.Application.Features.SetInventory;
using Viatica.Kernel;
using Viatica.Kernel.Web;

namespace Viatica.Cars.Api;

/// <summary>
/// Source-generated JSON for every body the service reads or writes, with the same settings as the shared options
/// (camelCase, enums as strings, nulls left out, a missing required member does not bind).
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LocationBody))]
[JsonSerializable(typeof(LocationView))]
[JsonSerializable(typeof(PagedResponse<LocationView>))]
[JsonSerializable(typeof(CategoryBody))]
[JsonSerializable(typeof(CategoryView))]
[JsonSerializable(typeof(PagedResponse<CategoryView>))]
[JsonSerializable(typeof(PartnerSettingsBody))]
[JsonSerializable(typeof(PartnerSettingsView))]
[JsonSerializable(typeof(InventoryBody))]
[JsonSerializable(typeof(InventoryChange))]
[JsonSerializable(typeof(IReadOnlyList<InventoryDayView>))]
[JsonSerializable(typeof(QuoteBody))]
[JsonSerializable(typeof(QuoteView))]
[JsonSerializable(typeof(Money))]
internal sealed partial class CarsJsonContext : JsonSerializerContext;
