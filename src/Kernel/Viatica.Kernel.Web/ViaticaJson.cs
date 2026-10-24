using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viatica.Kernel.Web;

/// <summary>
/// The JSON rules every service uses: camelCase, enums as strings, nulls left out, and strict reading of
/// required and non-nullable members.
/// </summary>
/// <remarks>
/// A service puts its own <see cref="JsonSerializerContext"/> in front with
/// <c>options.TypeInfoResolverChain.Insert(0, MyContext.Default)</c> and annotates it with
/// <c>[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true,
/// DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]</c> so its generated code agrees with these
/// options and keeps the fast path.
/// </remarks>
public static class ViaticaJson
{
    public static void Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter());

        // A request body missing a required field, or carrying null for a non-nullable one, fails to bind and
        // becomes a 400 instead of reaching the handler as a default value.
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;

        options.TypeInfoResolverChain.Insert(0, KernelJsonContext.Default);
    }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(Operation))]
[JsonSerializable(typeof(Money))]
[JsonSerializable(typeof(RepeatableResponse))]
internal sealed partial class KernelJsonContext : JsonSerializerContext;
