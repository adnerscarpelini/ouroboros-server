extern alias backoffice;

using System.Reflection;
using backoffice::Ouroboros.Backoffice.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.ArchitectureTests;

// Os controllers ficam nos hosts: os módulos expõem só casos de uso (Contracts), e cada host registra
// apenas os controllers do próprio projeto.
public sealed class HostSeparationTests(OuroborosApiFactory external, BackofficeApiFactory backoffice)
    : IClassFixture<OuroborosApiFactory>, IClassFixture<BackofficeApiFactory>
{
    private static readonly Assembly ExternalAssembly = typeof(Program).Assembly;
    private static readonly Assembly BackofficeAssembly = typeof(BackofficeApiEntryPoint).Assembly;

    [Theory]
    [MemberData(nameof(ModuleVisibilityTests.ModuleAssemblyNames), MemberType = typeof(ModuleVisibilityTests))]
    public void Modules_do_not_declare_controllers(string moduleAssemblyName)
    {
        var controllers = Assembly.Load(moduleAssemblyName).GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .Select(type => type.FullName);

        Assert.Empty(controllers);
    }

    [Fact]
    public void External_host_does_not_register_backoffice_controllers()
    {
        Assert.DoesNotContain(ControllerAssembliesOf(external.Services), assembly => assembly == BackofficeAssembly);
    }

    [Fact]
    public void Backoffice_host_does_not_register_external_controllers()
    {
        Assert.DoesNotContain(ControllerAssembliesOf(backoffice.Services), assembly => assembly == ExternalAssembly);
    }

    // A descoberta padrão ignora controllers internal sem avisar; nos hosts, todo controller é público.
    [Fact]
    public void Host_controllers_are_public()
    {
        var hidden = new[] { ExternalAssembly, BackofficeAssembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract && !type.IsPublic)
            .Select(type => type.FullName);

        Assert.Empty(hidden);
    }

    // Os módulos não publicam controllers, então não devem aparecer como application part de nenhum host.
    [Fact]
    public void Modules_are_not_application_parts()
    {
        var moduleParts = new[] { external.Services, backoffice.Services }
            .SelectMany(services => services.GetRequiredService<ApplicationPartManager>().ApplicationParts)
            .Select(part => part.Name)
            .Where(name => name.StartsWith("Ouroboros.Modules.", StringComparison.Ordinal));

        Assert.Empty(moduleParts);
    }

    private static IEnumerable<Assembly> ControllerAssembliesOf(IServiceProvider services) =>
        services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Select(action => action.ControllerTypeInfo.Assembly)
            .Distinct();
}
