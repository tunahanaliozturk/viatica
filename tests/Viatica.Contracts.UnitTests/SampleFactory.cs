using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Viatica.Contracts.UnitTests;

/// <summary>
/// Builds one deterministic instance of any contract record by walking its primary constructor. The values are
/// derived from parameter names, so a snapshot changes only when a contract changes.
/// </summary>
internal static class SampleFactory
{
    private static readonly DateTimeOffset Instant = new(2026, 12, 1, 9, 30, 0, TimeSpan.Zero);

    public static object Create(Type type) => Create(type, type.Name);

    private static object Create(Type type, string name)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
        {
            return name.Equals("currency", StringComparison.OrdinalIgnoreCase) ? "EUR" : name;
        }

        if (underlying == typeof(Guid))
        {
            return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));
        }

        if (underlying == typeof(int))
        {
            return 2;
        }

        if (underlying == typeof(long))
        {
            return 7L;
        }

        if (underlying == typeof(double))
        {
            return 38.7223;
        }

        if (underlying == typeof(decimal))
        {
            return 312.50m;
        }

        if (underlying == typeof(DateOnly))
        {
            return new DateOnly(2026, 12, 1);
        }

        if (underlying == typeof(TimeOnly))
        {
            return new TimeOnly(15, 0);
        }

        if (underlying == typeof(DateTimeOffset))
        {
            return Instant;
        }

        if (underlying == typeof(Uri))
        {
            return new Uri("https://cdn.viatica.local/" + name.ToLowerInvariant());
        }

        if (underlying.IsEnum)
        {
            return Enum.GetValues(underlying).GetValue(0)!;
        }

        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            Type element = underlying.GetGenericArguments()[0];
            Array array = Array.CreateInstance(element, 1);
            array.SetValue(Create(element, name), 0);
            return array;
        }

        ConstructorInfo constructor = underlying.GetConstructors().OrderByDescending(static c => c.GetParameters().Length).First();
        object?[] arguments = [.. constructor.GetParameters().Select(static p => Create(p.ParameterType, p.Name!))];
        return constructor.Invoke(arguments);
    }
}
