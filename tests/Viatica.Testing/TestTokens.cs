using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Viatica.Kernel.Web;

namespace Viatica.Testing;

/// <summary>
/// Tokens shaped like Keycloak's (sub, realm_access.roles, partnerId, aud) but signed with a key the test owns, so
/// service tests need no Keycloak and never trip its brute-force lockout. Give the host
/// <c>Viatica:Auth:TestSigningKey = TestTokens.SigningKeyBase64</c>.
/// </summary>
public static class TestTokens
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    public static string SigningKeyBase64 { get; } = Convert.ToBase64String(Key);

    public static string Traveller(string audience, Guid? subject = null) =>
        Mint(audience, subject ?? Guid.CreateVersion7(), [Roles.Traveller]);

    public static string Partner(string audience, Guid partnerId, Guid? subject = null) =>
        Mint(audience, subject ?? Guid.CreateVersion7(), [Roles.Partner], partnerId);

    public static string Admin(string audience, Guid? subject = null) =>
        Mint(audience, subject ?? Guid.CreateVersion7(), [Roles.Admin]);

    public static string Mint(string audience, Guid subject, IReadOnlyList<string> roles, Guid? partnerId = null)
    {
        Dictionary<string, object> claims = new()
        {
            ["sub"] = subject.ToString(),
            ["realm_access"] = JsonSerializer.SerializeToElement(new Dictionary<string, IReadOnlyList<string>> { ["roles"] = roles }),
        };

        if (partnerId is { } id)
        {
            claims[ViaticaAuthentication.PartnerIdClaim] = id.ToString();
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ViaticaAuthentication.TestIssuer,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Key), SecurityAlgorithms.HmacSha256),
        });
    }
}
