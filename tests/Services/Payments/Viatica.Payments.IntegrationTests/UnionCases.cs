using System.Runtime.CompilerServices;

namespace Viatica.Payments.IntegrationTests;

internal static class UnionCases
{
    /// <summary>
    /// The case a union holds, as that case. A union value is a struct around its case, so asserting on the union
    /// itself would only ever see the union type.
    /// </summary>
    public static T Case<T>(this IUnion union)
        where T : class
    {
        object? value = union.Value;
        value.ShouldBeOfType<T>();
        return (T)value;
    }
}
