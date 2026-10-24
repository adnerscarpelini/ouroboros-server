using Ouroboros.ArchitectureTests.Dependencies;

namespace Ouroboros.ArchitectureTests;

// O compilador impede que outro projeto use tipos internal de um serviço. Estes testes garantem as duas condições de
// que essa proteção depende: nenhum tipo fora das exceções vira public e nenhum serviço abre seus internals para outro
// projeto de src com InternalsVisibleTo. Os serviços vêm da árvore de src (SourceTree), então um serviço novo entra
// nos testes assim que o projeto existir.
public sealed class ModuleVisibilityTests
{
    public static TheoryData<string> ModuleAssemblyNames =>
    [
        .. SourceTree.Current.Services.Select(project => project.Name),
    ];

    [Fact]
    public void Services_are_discovered()
    {
        Assert.NotEmpty(ModuleAssemblyNames);
    }

    [Theory]
    [MemberData(nameof(ModuleAssemblyNames))]
    public void Module_exposes_only_DependencyInjection_and_Contracts(string moduleAssemblyName)
    {
        var dependencyInjectionType = $"{moduleAssemblyName}.DependencyInjection";
        var contractsNamespace = $"{moduleAssemblyName}.Contracts";

        var unexpectedPublicTypes = Scan(moduleAssemblyName).Types
            .Where(type => type.IsExported)
            .Where(type => type.Id.FullName != dependencyInjectionType)
            .Where(type => type.Id.Namespace != contractsNamespace
                && !type.Id.Namespace.StartsWith($"{contractsNamespace}.", StringComparison.Ordinal))
            .Where(type => !IsGeneratedMigration(type))
            .Select(type => type.Id.FullName);

        Assert.Empty(unexpectedPublicTypes);
    }

    // O dotnet ef gera as migrations como public. Elas só são usadas pelo EF via reflexão e não
    // expõem nada que outro serviço possa consumir, então são a única exceção à regra.
    private static bool IsGeneratedMigration(DefinedType type) =>
        type.BaseType == "Microsoft.EntityFrameworkCore.Migrations.Migration";

    [Theory]
    [MemberData(nameof(ModuleAssemblyNames))]
    public void Module_does_not_grant_internals_to_other_source_projects(string moduleAssemblyName)
    {
        var sourceProjects = SourceTree.Current.Projects.Projects.Select(project => project.Name);

        Assert.Empty(Scan(moduleAssemblyName).InternalsVisibleTo.Intersect(sourceProjects));
    }

    private static AssemblyScan Scan(string assemblyName) =>
        SourceTree.Current.Scans.Single(scan => scan.Name == assemblyName);
}
