using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.IntegrationTests;

// Os DbContexts são internal em cada módulo. Eles são encontrados por reflexão nos assemblies de
// módulo referenciados pela Api e resolvidos pela DI, então um módulo novo entra sem mudar os testes.
internal static class ModuleDbContexts
{
    private const string ModuleAssemblyPrefix = "Ouroboros.Modules.";

    public static IReadOnlyList<DbContext> Resolve(IServiceProvider services) =>
        [.. Types().Select(type => (DbContext)services.GetRequiredService(type))];

    private static IEnumerable<Type> Types() =>
        typeof(Program).Assembly.GetReferencedAssemblies()
            .Where(reference => reference.Name?.StartsWith(ModuleAssemblyPrefix, StringComparison.Ordinal) == true)
            .Select(Assembly.Load)
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(typeof(DbContext)));
}
