using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Viatica.Flights.Infrastructure;
using Viatica.Flights.Infrastructure.Suppliers;

namespace Viatica.Flights.UnitTests;

/// <summary>A service that is configured wrongly says so when it starts, and never prints the secret it was given.</summary>
public sealed class ConfigurationTests
{
    private const string Key32 = "dmlhdGljYS1kZXYtcGFzc2VuZ2VyLWtleS0wMDAwMDA=";

    private static Dictionary<string, string?> Good() => new()
    {
        ["ConnectionStrings:Flights"] = "Host=db;Database=flights;Username=flights;Password=flights",
        ["ConnectionStrings:Valkey"] = "valkey:6379",
        ["Kafka:BootstrapServers"] = "kafka:9092",
        ["Viatica:Pii:PassengerKey"] = Key32,
        ["Viatica:Pii:StorageKey"] = Key32,
        ["Flights:Suppliers:Airlines:0:Code"] = "ZC",
        ["Flights:Suppliers:Airlines:0:BaseUrl"] = "http://zc.test/ndc/ZC/",
    };

    private static ServiceCollection Register(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlightsInfrastructure(configuration);
        return services;
    }

    [Fact]
    public void A_complete_configuration_registers()
    {
        Should.NotThrow(() => Register(Good()));
    }

    [Theory]
    [InlineData("ConnectionStrings:Flights")]
    [InlineData("ConnectionStrings:Valkey")]
    [InlineData("Kafka:BootstrapServers")]
    [InlineData("Viatica:Pii:PassengerKey")]
    [InlineData("Viatica:Pii:StorageKey")]
    public void A_missing_setting_stops_the_host_and_names_the_setting(string key)
    {
        Dictionary<string, string?> settings = Good();
        settings.Remove(key);

        Should.Throw<InvalidOperationException>(() => Register(settings)).Message.ShouldContain(key);
    }

    [Fact]
    public void A_key_of_the_wrong_size_is_refused_without_being_repeated()
    {
        Dictionary<string, string?> settings = Good();
        settings["Viatica:Pii:PassengerKey"] = Convert.ToBase64String("tooshortkey-hunter2"u8);

        ArgumentException error = Should.Throw<ArgumentException>(() => Register(settings));

        error.Message.ShouldNotContain("hunter2");
        error.Message.ShouldNotContain(settings["Viatica:Pii:PassengerKey"]!);
    }

    [Theory]
    [InlineData("Flights:Suppliers:Airlines:0:BaseUrl", "not a url")]
    [InlineData("Flights:Suppliers:Airlines:0:Code", "zc")]
    [InlineData("Flights:Suppliers:RequestTimeout", "00:00:00")]
    [InlineData("Flights:Suppliers:CircuitBreaker:MinimumThroughput", "1")]
    [InlineData("Flights:Suppliers:CircuitBreaker:FailureRatio", "0")]
    public void Airline_settings_that_cannot_work_fail_when_they_are_first_read(string key, string value)
    {
        Dictionary<string, string?> settings = Good();
        settings[key] = value;
        using ServiceProvider provider = Register(settings).BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SuppliersOptions>>().Value);
    }

    [Fact]
    public void Two_airlines_cannot_share_a_code()
    {
        Dictionary<string, string?> settings = Good();
        settings["Flights:Suppliers:Airlines:1:Code"] = "ZC";
        settings["Flights:Suppliers:Airlines:1:BaseUrl"] = "http://other.test/";
        using ServiceProvider provider = Register(settings).BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SuppliersOptions>>().Value);
    }

    [Fact]
    public void The_defaults_are_the_figures_the_requirements_name()
    {
        using ServiceProvider provider = Register(Good()).BuildServiceProvider();

        provider.GetRequiredService<Application.FlightsOptions>().SearchDeadline.ShouldBe(TimeSpan.FromSeconds(2.5));
        provider.GetRequiredService<IOptions<SuppliersOptions>>().Value.Airlines.Select(static a => a.Code).ShouldBe(["ZC"]);
    }
}
