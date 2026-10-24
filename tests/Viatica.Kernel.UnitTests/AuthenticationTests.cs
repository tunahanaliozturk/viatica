using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Viatica.Kernel.Web;
using Viatica.Testing;

namespace Viatica.Kernel.UnitTests;

public sealed class AuthenticationTests : IAsyncLifetime
{
    private const string Audience = "stays-api";
    private WebApplication? app;
    private HttpClient client = null!;

    public async ValueTask InitializeAsync()
    {
        app = Build("Development", TestTokens.SigningKeyBase64);
        app.UseViaticaApi();
        app.MapGet("/partner", static (ClaimsPrincipal user) => user.PartnerId().ToString()).RequireAuthorization(Policies.Partner);
        app.MapGet("/admin", static () => "ok").RequireAuthorization(Policies.Admin);
        await app.StartAsync();
        client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_request_without_a_token_gets_401_in_the_envelope()
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri("/partner?api-version=2026-11-01", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("\"code\":\"Unauthorized\"");
    }

    [Fact]
    public async Task Realm_roles_and_the_partner_claim_reach_the_policy()
    {
        Guid partnerId = Guid.CreateVersion7();

        using HttpResponseMessage response = await Get("/partner", TestTokens.Partner(Audience, partnerId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe(partnerId.ToString());
    }

    [Fact]
    public async Task A_traveller_is_forbidden_from_partner_endpoints()
    {
        using HttpResponseMessage response = await Get("/partner", TestTokens.Traveller(Audience));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_partner_role_without_a_partner_id_is_not_a_partner()
    {
        using HttpResponseMessage response = await Get("/partner", TestTokens.Mint(Audience, Guid.CreateVersion7(), [Roles.Partner]));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_token_for_another_service_is_refused()
    {
        using HttpResponseMessage response = await Get("/admin", TestTokens.Admin("payments-api"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void The_test_signing_key_is_refused_in_production() =>
        Should.Throw<InvalidOperationException>(() => Build("Production", TestTokens.SigningKeyBase64));

    private async Task<HttpResponseMessage> Get(string path, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path + "?api-version=2026-11-01");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static WebApplication Build(string environment, string signingKey)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration["Viatica:Api:SkipTokenKey"] = Keys.SkipTokenBase64;
        builder.Configuration["Viatica:Api:SupportedApiVersions:0"] = "2026-11-01";
        builder.Configuration["Viatica:Auth:TestSigningKey"] = signingKey;
        builder.AddViaticaApi();
        builder.AddViaticaAuth(Audience);
        return builder.Build();
    }
}
