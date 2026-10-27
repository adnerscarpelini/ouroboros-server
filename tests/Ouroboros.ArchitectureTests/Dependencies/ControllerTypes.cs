using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Ouroboros.ArchitectureTests.Dependencies;

internal static class ControllerTypes
{
    public static IReadOnlySet<TypeId> Discover(IEnumerable<Assembly> assemblies)
    {
        var sourceAssemblies = assemblies.ToHashSet();
        var feature = new ControllerFeature();
        new ControllerFeatureProvider().PopulateFeature(sourceAssemblies.Select(assembly => new AssemblyPart(assembly)), feature);
        var controllers = new HashSet<TypeId>();
        foreach (var controller in feature.Controllers)
        {
            // Inclui bases próprias abstratas: dependências herdadas também fazem parte do controller.
            for (Type? type = controller.AsType(); type is not null && sourceAssemblies.Contains(type.Assembly); type = type.BaseType)
            {
                controllers.Add(Id(type));
            }
        }
        return controllers;
    }

    internal static TypeId Id(Type type) => new(type.Assembly.GetName().Name!, type.Namespace ?? string.Empty, type.Name);
}
