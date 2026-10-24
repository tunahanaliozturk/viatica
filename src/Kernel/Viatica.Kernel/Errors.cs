using System.Text.Json.Serialization;

namespace Viatica.Kernel;

/// <summary>
/// Error codes every service shares. A service adds its own domain codes (<c>SoldOut</c>, <c>HoldExpired</c>) and
/// documents them in <c>docs/api/errors.md</c>; these are the ones the platform itself produces.
/// </summary>
/// <remarks>Codes are contract: clients switch on them. Messages are for people and may change.</remarks>
public static class ApiErrorCode
{
    public const string InvalidRequest = nameof(InvalidRequest);
    public const string NotFound = nameof(NotFound);
    public const string Conflict = nameof(Conflict);
    public const string PreconditionFailed = nameof(PreconditionFailed);
    public const string PreconditionRequired = nameof(PreconditionRequired);
    public const string MissingApiVersionParameter = nameof(MissingApiVersionParameter);
    public const string UnsupportedApiVersionValue = nameof(UnsupportedApiVersionValue);
    public const string InvalidSkipToken = nameof(InvalidSkipToken);
    public const string InvalidFilter = nameof(InvalidFilter);
    public const string InvalidOrderBy = nameof(InvalidOrderBy);
    public const string TooManyRequests = nameof(TooManyRequests);
    public const string InternalError = nameof(InternalError);

    // Produced by the framework rather than by application code (authentication, routing, content negotiation),
    // named so the envelope never has to fall back to a vague code for a status the platform does emit.
    public const string Unauthorized = nameof(Unauthorized);
    public const string Forbidden = nameof(Forbidden);
    public const string MethodNotAllowed = nameof(MethodNotAllowed);
    public const string UnsupportedMediaType = nameof(UnsupportedMediaType);

    /// <summary>A validation rule failed without naming its own code.</summary>
    public const string InvalidValue = nameof(InvalidValue);
}

/// <summary>
/// What kind of failure an error is, which the web layer turns into an HTTP status. Application code picks a
/// category without knowing HTTP, so the same handler can run behind a Kafka consumer as well.
/// </summary>
public enum ErrorCategory
{
    /// <summary>The request breaks a rule the caller could have known (400).</summary>
    InvalidRequest,

    /// <summary>The resource does not exist or the caller may not know that it does (404).</summary>
    NotFound,

    /// <summary>The request clashes with current state: sold out, already cancelled (409).</summary>
    Conflict,

    /// <summary>The resource changed since the caller last read it (412).</summary>
    PreconditionFailed,

    /// <summary>The caller must say which version it is changing (428).</summary>
    PreconditionRequired,

    /// <summary>The caller is over a rate limit (429).</summary>
    TooManyRequests,
}

/// <summary>
/// One error in the Microsoft REST API Guidelines shape. The same type serves as the top-level error and as each
/// entry of <see cref="Details"/>.
/// </summary>
public sealed record ApiError(string Code, string Message)
{
    /// <summary>What the error is about: a field path such as <c>items[0].checkOut</c>, or a parameter.</summary>
    public string? Target { get; init; }

    public IReadOnlyList<ApiError>? Details { get; init; }

    [JsonPropertyName("innererror")]
    public InnerError? InnerError { get; init; }
}

/// <summary>A more specific code under an error, nested as deep as the service needs.</summary>
public sealed record InnerError(string Code)
{
    [JsonPropertyName("innererror")]
    public InnerError? Inner { get; init; }
}

/// <summary>
/// Thrown by application code to end a request with a specific error. The web layer turns it into the error
/// envelope; nothing here knows about HTTP.
/// </summary>
/// <remarks>
/// An exception rather than a result type because the failures it carries (not found, sold out, stale version)
/// end the use case on the spot, and every handler would otherwise repeat the same early return.
/// Outcomes that are not errors, like a hold that could not be placed, are modelled as return values instead.
/// </remarks>
public sealed class ApiErrorException : Exception
{
    public ApiErrorException(ErrorCategory category, ApiError error)
        : base(error?.Message)
    {
        ArgumentNullException.ThrowIfNull(error);
        Category = category;
        Error = error;
    }

    public ErrorCategory Category { get; }

    public ApiError Error { get; }

    /// <summary>A domain error with its own code, such as <c>SoldOut</c> under <see cref="ErrorCategory.Conflict"/>.</summary>
    public static ApiErrorException Domain(ErrorCategory category, string code, string message, string? target = null) =>
        new(category, new ApiError(code, message) { Target = target });

    public static ApiErrorException Invalid(string code, string message, string? target = null) =>
        Domain(ErrorCategory.InvalidRequest, code, message, target);

    public static ApiErrorException NotFound(string message = "The resource was not found.") =>
        Domain(ErrorCategory.NotFound, ApiErrorCode.NotFound, message);

    public static ApiErrorException Conflict(string code, string message) =>
        Domain(ErrorCategory.Conflict, code, message);

    public static ApiErrorException PreconditionFailed(
        string message = "The resource has changed since it was read. Read it again and retry with the new ETag.") =>
        Domain(ErrorCategory.PreconditionFailed, ApiErrorCode.PreconditionFailed, message);

    /// <summary>One <c>400 InvalidRequest</c> carrying every field that failed, so a form can mark them all at once.</summary>
    public static ApiErrorException Validation(IReadOnlyList<ApiError> details) =>
        new(ErrorCategory.InvalidRequest, new ApiError(ApiErrorCode.InvalidRequest, "The request is invalid.")
        {
            Details = details,
        });
}
