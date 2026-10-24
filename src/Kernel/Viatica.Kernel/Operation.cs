using System.Text.Json.Serialization;

namespace Viatica.Kernel;

/// <summary>The states of a long running operation, as the Microsoft REST API Guidelines name them.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperationStatus>))]
public enum OperationStatus
{
    NotStarted,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>
/// The body of <c>GET</c> on an <c>Operation-Location</c> URL: where a long running operation such as checkout
/// stands, its error once it has failed, and where the result lives once it has succeeded.
/// </summary>
public sealed record Operation(string Id, OperationStatus Status)
{
    public ApiError? Error { get; init; }

    public string? ResourceLocation { get; init; }
}
