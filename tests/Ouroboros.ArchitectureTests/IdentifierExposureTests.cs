using System.Reflection;
using Ouroboros.ArchitectureTests.Dependencies;

namespace Ouroboros.ArchitectureTests;

// O Id (long) de uma entidade nunca sai do módulo: Contracts e DTOs de resposta usam o ExternalId.
public sealed class IdentifierExposureTests
{
    [Theory]
    [MemberData(nameof(ModuleVisibilityTests.ModuleAssemblyNames), MemberType = typeof(ModuleVisibilityTests))]
    public void Contracts_and_responses_do_not_expose_long_identifiers(string moduleAssemblyName)
    {
        var contractsNamespace = $"{moduleAssemblyName}.Contracts";

        var violations = SourceTree.Current.Load(moduleAssemblyName).GetTypes()
            .Where(type => IsInNamespace(type, contractsNamespace) || type.Name.EndsWith("Response", StringComparison.Ordinal))
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => IsLong(property.PropertyType) && property.Name.EndsWith("Id", StringComparison.Ordinal))
                .Select(property => $"{type.FullName}.{property.Name}"));

        Assert.Empty(violations);
    }

    private static bool IsInNamespace(Type type, string @namespace) =>
        type.Namespace == @namespace || type.Namespace?.StartsWith($"{@namespace}.", StringComparison.Ordinal) == true;

    private static bool IsLong(Type type) => (Nullable.GetUnderlyingType(type) ?? type) == typeof(long);
}
