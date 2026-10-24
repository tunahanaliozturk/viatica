using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Viatica.Kernel.Web;

/// <summary>Role names as Keycloak issues them in <c>realm_access.roles</c>.</summary>
public static class Roles
{
    public const string Traveller = "traveller";
    public const string Partner = "partner";
    public const string Admin = "admin";
}

/// <summary>Authorization policies every service registers through <see cref="ViaticaAuthentication"/>.</summary>
public static class Policies
{
    public const string Traveller = "traveller";

    /// <summary>The partner role and a <c>partnerId</c> claim; ownership of the resource is still checked per request.</summary>
    public const string Partner = "partner";

    public const string Admin = "admin";
}

public static class ViaticaAuthentication
{
    public const string Section = "Viatica:Auth";

    /// <summary>The issuer of tokens minted by the test suites with <c>Viatica:Auth:TestSigningKey</c>.</summary>
    public const string TestIssuer = "viatica-tests";

    public const string PartnerIdClaim = "partnerId";

    /// <summary>
    /// JWT bearer validation against Keycloak for one service audience (<c>stays-api</c>, ...). The gateway checks
    /// tokens too, but every service checks again: a request that reaches a service by another route is not
    /// trusted because it came from inside.
    /// </summary>
    /// <remarks>
    /// Settings under <c>Viatica:Auth</c>: <c>Authority</c> (where to fetch signing keys), optionally
    /// <c>ValidIssuer</c> when the public issuer differs from the address services reach Keycloak on, and
    /// <c>TestSigningKey</c> (base64, 32 bytes or more) which replaces Keycloak with a symmetric key for tests.
    /// The test key is refused in Production, so a misconfigured deployment fails at start-up rather than
    /// accepting self-minted tokens.
    /// </remarks>
    public static TBuilder AddViaticaAuth<TBuilder>(this TBuilder builder, string audience)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);

        IConfigurationSection settings = builder.Configuration.GetSection(Section);
        string? testKey = settings["TestSigningKey"];

        // Checked here, not inside the options callback, which only runs on the first authenticated request.
        if (testKey is not null && builder.Environment.IsProduction())
        {
            throw new InvalidOperationException("Viatica:Auth:TestSigningKey must not be set in Production.");
        }

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Keep Keycloak's claim names (sub, realm_access) instead of the legacy WS-Federation URIs.
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "sub";
                options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                options.TokenValidationParameters.ValidAudience = audience;
                options.Events = new JwtBearerEvents { OnTokenValidated = static context => AddRealmRoles(context) };

                if (testKey is not null)
                {
                    options.TokenValidationParameters.ValidIssuer = TestIssuer;
                    options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(testKey));
                    return;
                }

                options.Authority = settings["Authority"]
                    ?? throw new InvalidOperationException("Viatica:Auth:Authority is required.");
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment() && options.Authority.StartsWith("https", StringComparison.Ordinal);

                if (settings["ValidIssuer"] is { } issuer)
                {
                    options.TokenValidationParameters.ValidIssuer = issuer;
                }
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Traveller, static policy => policy.RequireRole(Roles.Traveller))
            .AddPolicy(Policies.Partner, static policy => policy.RequireRole(Roles.Partner).RequireClaim(PartnerIdClaim))
            .AddPolicy(Policies.Admin, static policy => policy.RequireRole(Roles.Admin));

        return builder;
    }

    /// <summary>The caller's Keycloak subject id.</summary>
    public static Guid SubjectId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Guid.Parse(user.FindFirstValue("sub") ?? throw new InvalidOperationException("The token has no sub claim."));
    }

    /// <summary>The partner the caller acts for, or null for anyone who is not a partner.</summary>
    public static Guid? PartnerId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Guid.TryParse(user.FindFirstValue(PartnerIdClaim), out Guid id) ? id : null;
    }

    public static bool IsAdmin(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.IsInRole(Roles.Admin);
    }

    // Keycloak puts realm roles in a JSON object claim; ASP.NET Core authorization wants one claim per role.
    private static Task AddRealmRoles(TokenValidatedContext context)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity || identity.FindFirst("realm_access") is not { } claim)
        {
            return Task.CompletedTask;
        }

        using JsonDocument document = JsonDocument.Parse(claim.Value);
        if (document.RootElement.TryGetProperty("roles", out JsonElement roles) && roles.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement role in roles.EnumerateArray())
            {
                if (role.GetString() is { } name)
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, name));
                }
            }
        }

        return Task.CompletedTask;
    }
}
