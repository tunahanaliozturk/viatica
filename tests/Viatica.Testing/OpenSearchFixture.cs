using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Viatica.Testing;

/// <summary>
/// One OpenSearch node (single node, security plugin off, 512 MB heap) for a whole test assembly. The image is the
/// version compose.yaml runs, pinned by digest.
/// </summary>
/// <remarks>
/// <para>
/// Register with <c>[assembly: AssemblyFixture(typeof(OpenSearchFixture))]</c>. The service under test names its
/// indices (<c>stays</c>, <c>cars</c>) itself, so tests that use them must not overlap in time: switch off test
/// parallelisation for the assembly and call <see cref="ResetAsync"/> first thing in each test class.
/// </para>
/// <para>
/// The heap is a quarter of what compose gives it. The seeded data of these tests is small; the 10,000 property
/// benchmark runs against compose's node.
/// </para>
/// </remarks>
public sealed class OpenSearchFixture : IAsyncLifetime
{
    private const int HttpPort = 9200;

    private readonly IContainer _container = new ContainerBuilder(
            "opensearchproject/opensearch:3.9.0@sha256:adfa61f85025d06b4aeb562e7e74fde7e31c437039c93c3862c17e9acebd6c7c")
        .WithEnvironment("discovery.type", "single-node")
        .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
        .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
        .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
        .WithPortBinding(HttpPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
            .ForPort(HttpPort)
            .ForPath("/_cluster/health")))
        .Build();

    private readonly HttpClient _http = new();

    public Uri Uri => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // Deleting by wildcard is refused by default. ResetAsync needs it, and a test node is the one place it is fine.
        // Auto-creation of an index called stays or cars is off, as in compose: a write to an alias that does not
        // exist yet must fail, not quietly create an index with that name, which would make the alias impossible to
        // create afterwards.
        using var settings = new StringContent(
            """{ "persistent": { "action.destructive_requires_name": false, "action.auto_create_index": "-stays,-cars,+*" } }""",
            System.Text.Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await _http.PutAsync(new Uri(Uri, "/_cluster/settings"), settings);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>Deletes every index and alias, so the next test starts on an empty node.</summary>
    public async Task ResetAsync()
    {
        using HttpResponseMessage response = await _http.DeleteAsync(new Uri(Uri, "/*"));
        response.EnsureSuccessStatusCode();
    }
}
