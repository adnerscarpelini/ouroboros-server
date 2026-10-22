using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ouroboros.ArchitectureTests;

// O compilador impede Ouroboros.Api de usar tipos internal dos módulos. Estes testes garantem
// as duas condições de que essa proteção depende: nenhum tipo fora das exceções vira public e
// nenhum módulo abre seus internals para a Api com InternalsVisibleTo.
public sealed class ModuleVisibilityTests
{
    private const string ApiAssemblyName = "Ouroboros.Api";

    private const string ModuleAssemblyPrefix = "Ouroboros.Modules.";

    // Os módulos são descobertos pelas referências do host, então um módulo novo entra nos testes
    // assim que for registrado na Api.
    public static TheoryData<string> ModuleAssemblyNames =>
    [
        .. Assembly.Load(ApiAssemblyName)
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .Where(name => name.StartsWith(ModuleAssemblyPrefix, StringComparison.Ordinal)),
    ];

    [Fact]
    public void Api_references_modules()
    {
        Assert.NotEmpty(ModuleAssemblyNames);
    }

    [Theory]
    [MemberData(nameof(ModuleAssemblyNames))]
    public void Module_exposes_only_DependencyInjection_and_Contracts(string moduleAssemblyName)
    {
        var module = Assembly.Load(moduleAssemblyName);
        var dependencyInjectionType = $"{moduleAssemblyName}.DependencyInjection";
        var contractsNamespace = $"{moduleAssemblyName}.Contracts";

        var unexpectedPublicTypes = module.GetExportedTypes()
            .Where(type => type.FullName != dependencyInjectionType)
            .Where(type => type.Namespace != contractsNamespace
                && type.Namespace?.StartsWith($"{contractsNamespace}.", StringComparison.Ordinal) != true)
            .Select(type => type.FullName);

        Assert.Empty(unexpectedPublicTypes);
    }

    [Theory]
    [MemberData(nameof(ModuleAssemblyNames))]
    public void Module_does_not_grant_internals_to_Api(string moduleAssemblyName)
    {
        var friendAssemblies = Assembly.Load(moduleAssemblyName)
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => new AssemblyName(attribute.AssemblyName).Name);

        Assert.DoesNotContain(ApiAssemblyName, friendAssemblies);
    }
}
