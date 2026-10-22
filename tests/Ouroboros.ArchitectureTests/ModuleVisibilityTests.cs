using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ouroboros.ArchitectureTests;

// O compilador impede Ouroboros.Api de usar tipos internal dos módulos. Estes testes garantem
// as duas condições de que essa proteção depende: nenhum tipo fora das exceções vira public e
// nenhum módulo abre seus internals para a Api com InternalsVisibleTo.
public sealed class ModuleVisibilityTests
{
    // Os dois hosts: nenhum deles pode enxergar os tipos internos dos módulos.
    private static readonly string[] HostAssemblyNames = ["Ouroboros.Api", "Ouroboros.Backoffice.Api"];

    private const string ModuleAssemblyPrefix = "Ouroboros.Modules.";

    // Os módulos são descobertos pelas referências dos hosts, então um módulo novo entra nos testes
    // assim que for registrado em qualquer um deles.
    public static TheoryData<string> ModuleAssemblyNames =>
    [
        .. HostAssemblyNames
            .SelectMany(host => Assembly.Load(host).GetReferencedAssemblies())
            .Select(reference => reference.Name)
            .OfType<string>()
            .Where(name => name.StartsWith(ModuleAssemblyPrefix, StringComparison.Ordinal))
            .Distinct(),
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
            .Where(type => !IsGeneratedMigration(type))
            .Select(type => type.FullName);

        Assert.Empty(unexpectedPublicTypes);
    }

    // O dotnet ef gera as migrations como public. Elas só são usadas pelo EF via reflexão e não
    // expõem nada que outro módulo possa consumir, então são a única exceção à regra.
    private static bool IsGeneratedMigration(Type type) =>
        type.BaseType?.FullName == "Microsoft.EntityFrameworkCore.Migrations.Migration";

    [Theory]
    [MemberData(nameof(ModuleAssemblyNames))]
    public void Module_does_not_grant_internals_to_hosts(string moduleAssemblyName)
    {
        var friendAssemblies = Assembly.Load(moduleAssemblyName)
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => new AssemblyName(attribute.AssemblyName).Name);

        Assert.Empty(friendAssemblies.Intersect(HostAssemblyNames));
    }
}
