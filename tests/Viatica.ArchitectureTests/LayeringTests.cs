using System.Reflection;

namespace Viatica.ArchitectureTests;

/// <summary>
/// The layering of every service, and the walls between services, checked against what each compiled assembly
/// actually references rather than against project files. A project reference nothing uses does not show up
/// here, and a type used through a transitive reference does, which is the dependency that matters.
/// </summary>
/// <remarks>
/// Services are discovered from the assemblies next to the test, so a new service is covered the moment its
/// host is referenced by this project. Search has no Domain on purpose: it is a read model.
/// </remarks>
public sealed class LayeringTests
{
    // Concrete technologies the inner layers may not name. Application is allowed EF Core, which is provider
    // neutral; the provider, Kafka, OpenSearch and Redis stay in Infrastructure.
    private static readonly string[] Technologies =
    [
        "Npgsql",
        "Confluent.Kafka",
        "OpenSearch",
        "StackExchange.Redis",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.Hosting",
        "OpenTelemetry",
        "Viatica.Kernel.Web",
        "Viatica.Kernel.Messaging",
    ];

    private static readonly string[] Layers = ["Domain", "Application", "Infrastructure", "Api"];

    private static string[] Services { get; } =
    [
        .. Directory.EnumerateFiles(AppContext.BaseDirectory, "Viatica.*.Application.dll")
            .Select(static path => Path.GetFileNameWithoutExtension(path).Split('.')[1])
            .Order(StringComparer.Ordinal),
    ];

    private static Assembly? Layer(string service, string layer) =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, $"Viatica.{service}.{layer}.dll"))
            ? Assembly.Load($"Viatica.{service}.{layer}")
            : null;

    private static string[] References(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(static name => name.Name!)];

    private static string[] Projects(Assembly assembly) =>
        [.. References(assembly).Where(static name => name.StartsWith("Viatica.", StringComparison.Ordinal))];

    private static bool IsBaseLibrary(string name) =>
        name.StartsWith("System", StringComparison.Ordinal) || name is "netstandard" or "mscorlib";

    [Fact]
    public void The_domain_knows_only_the_kernel_and_its_guard_library()
    {
        string[] allowed = ["Viatica.Kernel", "Moongazing.OrionGuard"];

        foreach (string service in Services)
        {
            if (Layer(service, "Domain") is not { } domain)
            {
                continue;
            }

            References(domain)
                .Where(static name => !IsBaseLibrary(name))
                .ShouldAllBe(name => allowed.Contains(name), $"Viatica.{service}.Domain");
        }
    }

    [Fact]
    public void Neither_inner_layer_names_a_concrete_technology()
    {
        foreach (string service in Services)
        {
            foreach (string layer in (string[])["Domain", "Application"])
            {
                if (Layer(service, layer) is not { } inner)
                {
                    continue;
                }

                References(inner).ShouldNotContain(
                    name => Technologies.Any(technology => name.StartsWith(technology, StringComparison.Ordinal)),
                    $"Viatica.{service}.{layer} references a technology");
            }
        }
    }

    [Fact]
    public void The_application_does_not_know_infrastructure_or_the_host()
    {
        foreach (string service in Services)
        {
            Projects(Layer(service, "Application")!).ShouldNotContain(
                name => name == $"Viatica.{service}.Infrastructure" || name == $"Viatica.{service}.Api");
        }
    }

    [Fact]
    public void Infrastructure_knows_nothing_of_the_host()
    {
        foreach (string service in Services)
        {
            if (Layer(service, "Infrastructure") is { } infrastructure)
            {
                Projects(infrastructure).ShouldNotContain($"Viatica.{service}.Api");
            }
        }
    }

    [Fact]
    public void No_layer_of_a_service_references_another_service()
    {
        foreach (string service in Services)
        {
            string[] others = [.. Services.Where(other => other != service)];

            foreach (string layer in Layers)
            {
                if (Layer(service, layer) is not { } assembly)
                {
                    continue;
                }

                Projects(assembly).ShouldNotContain(
                    name => others.Any(other => name.StartsWith($"Viatica.{other}.", StringComparison.Ordinal)),
                    $"Viatica.{service}.{layer} reaches into another service");
            }
        }
    }

    [Fact]
    public void The_contracts_depend_on_nothing() =>
        References(Assembly.Load("Viatica.Contracts")).ShouldAllBe(static name => IsBaseLibrary(name));

    [Fact]
    public void The_kernel_knows_no_web_framework_and_no_infrastructure()
    {
        string[] allowed =
        [
            "Moongazing.OrionGuard",
            "Relevox.Contracts",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
        ];

        References(Assembly.Load("Viatica.Kernel"))
            .Where(static name => !IsBaseLibrary(name))
            .ShouldAllBe(name => allowed.Contains(name));
    }
}
