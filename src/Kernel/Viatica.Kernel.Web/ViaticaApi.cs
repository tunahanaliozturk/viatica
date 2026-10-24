using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Viatica.Kernel.Web;

/// <summary>The two calls every service host makes to follow the shared API conventions.</summary>
public static class ViaticaApi
{
    /// <summary>
    /// Registers the error envelope, JSON options, skiptoken signing and repeatability storage. Settings come from
    /// the <c>Viatica:Api</c> section, then from <paramref name="configure"/>; they are validated at start-up, so
    /// a missing skiptoken key stops the host instead of failing the first paged request.
    /// </summary>
    /// <remarks>
    /// Repeatability uses whatever <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> is
    /// registered. Register the Redis (Valkey) one in the service; the in-memory fallback added here only works
    /// for a single instance.
    /// </remarks>
    public static TBuilder AddViaticaApi<TBuilder>(this TBuilder builder, Action<ViaticaApiOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        IServiceCollection services = builder.Services;

        services.AddOptions<ViaticaApiOptions>()
            .BindConfiguration(ViaticaApiOptions.Section)
            .Configure(options => configure?.Invoke(options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ViaticaApiOptions>, ViaticaApiOptionsValidator>();

        services.AddExceptionHandler<ApiExceptionHandler>();
        services.ConfigureHttpJsonOptions(static options => ViaticaJson.Apply(options.SerializerOptions));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SkipTokenCodec>();
        services.AddDistributedMemoryCache();

        return builder;
    }

    /// <summary>
    /// Puts the middleware in the order the conventions need: errors outermost so every failure gets the envelope,
    /// then routing, authentication when it is registered, the <c>api-version</c> check, and repeatability last,
    /// because it scopes request ids to the authenticated caller. Call it before mapping endpoints and do not call
    /// <c>UseRouting</c> or <c>UseAuthentication</c> yourself.
    /// </summary>
    public static WebApplication UseViaticaApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The delegate only runs if the registered IExceptionHandler declines, which it does when the response
        // has already started; the middleware insists on a fallback either way.
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = static context => ErrorResponses.WriteAsync(context, 500, ErrorResponses.ForStatusCode(500)),
        });
        app.UseStatusCodePages(ErrorResponses.WriteForStatusCodeAsync);
        app.UseRouting();

        if (app.Services.GetService<IAuthenticationSchemeProvider>() is not null)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.UseMiddleware<ApiVersionMiddleware>();
        app.UseMiddleware<RepeatabilityMiddleware>();
        return app;
    }
}

internal sealed class ViaticaApiOptionsValidator : IValidateOptions<ViaticaApiOptions>
{
    public ValidateOptionsResult Validate(string? name, ViaticaApiOptions options)
    {
        string[] failures = [.. options.Validate()];
        return failures.Length == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
