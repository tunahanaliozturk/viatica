using System.Text.Json;
using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;

namespace Viatica.Kernel.Validation;

/// <summary>
/// Runs every OrionGuard validator registered for a request before its handler, and ends the request with one
/// <c>400 InvalidRequest</c> listing every failed rule when any fails.
/// </summary>
/// <remarks>
/// Validators run one after another, not in parallel: an async rule may use the request's scoped
/// <c>DbContext</c>, which is not safe to share across threads. Every validator runs even after one fails, so the
/// caller learns about all broken fields in one round trip.
/// </remarks>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        List<ApiError>? details = null;
        foreach (IValidator<TRequest> validator in validators)
        {
            GuardResult result = await validator.ValidateAsync(request, cancellationToken);
            if (!result.IsInvalid)
            {
                continue;
            }

            details ??= [];
            foreach (ValidationError error in result.Errors)
            {
                details.Add(ToDetail(error));
            }
        }

        return details is null ? await next(cancellationToken) : throw ApiErrorException.Validation(details);
    }

    /// <summary>
    /// <c>ValidationError(ParameterName, Message, ErrorCode)</c> becomes <c>{ target, message, code }</c>. The target
    /// is put in camelCase segment by segment, so <c>Items[0].CheckOut</c> names the JSON field
    /// <c>items[0].checkOut</c> the client actually sent.
    /// </summary>
    internal static ApiError ToDetail(ValidationError error) =>
        new(string.IsNullOrEmpty(error.ErrorCode) ? ApiErrorCode.InvalidValue : error.ErrorCode, error.Message)
        {
            Target = string.IsNullOrEmpty(error.ParameterName) ? null : CamelCasePath(error.ParameterName),
        };

    // The same policy the JSON options use, so "ID" becomes "id" here exactly as it does on the wire.
    internal static string CamelCasePath(string path) =>
        string.Join('.', path.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
}
