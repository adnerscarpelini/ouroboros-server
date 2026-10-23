using System.Reflection;

namespace Ouroboros.ArchitectureTests;

// BuildingBlocks é o código compartilhado pelos módulos (base de entidade e persistência). Ele não pode
// depender de módulos nem de hosts, senão viraria um atalho entre eles.
public sealed class BuildingBlocksTests
{
    [Fact]
    public void Building_blocks_do_not_reference_modules_or_hosts()
    {
        var references = Assembly.Load("Ouroboros.BuildingBlocks").GetReferencedAssemblies()
            .Select(reference => reference.Name ?? "")
            .Where(name => name.StartsWith("Ouroboros.", StringComparison.Ordinal));

        Assert.Empty(references);
    }

    [Fact]
    public void Building_blocks_domain_does_not_depend_on_entity_framework()
    {
        var domainTypes = Assembly.Load("Ouroboros.BuildingBlocks").GetTypes()
            .Where(type => type.Namespace == "Ouroboros.BuildingBlocks.Domain");

        var efDependencies = domainTypes
            .SelectMany(type => type.GetProperties().Select(property => property.PropertyType).Append(type.BaseType))
            .OfType<Type>()
            .Where(type => type.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true);

        Assert.Empty(efDependencies);
    }
}
