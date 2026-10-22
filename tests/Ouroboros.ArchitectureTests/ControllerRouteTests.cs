using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.ArchitectureTests;

// Sem grupos de rota, a separação por público depende de convenção. Este teste a verifica sobre as
// actions que o host realmente registra: cada rota de módulo usa o prefixo do próprio módulo, e o
// prefixo de backoffice aparece se e somente se o controller herda a AdminControllerBase do módulo.
// Só são vistos os controllers que o host descobre; ControllerDiscoveryTests garante que os
// controllers internal são descobertos, sem o que este teste passaria sem verificar nada.
public sealed class ControllerRouteTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ModuleAssemblyPrefix = "Ouroboros.Modules.";

    [Fact]
    public void Module_controllers_use_audience_routes_of_their_module()
    {
        var actions = factory.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.Assembly.GetName().Name?
                .StartsWith(ModuleAssemblyPrefix, StringComparison.Ordinal) == true);

        var violations = actions.Select(FindViolation).OfType<string>().ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static string? FindViolation(ControllerActionDescriptor action)
    {
        var controllerType = action.ControllerTypeInfo;
        var moduleAssemblyName = controllerType.Assembly.GetName().Name!;
        var module = moduleAssemblyName[ModuleAssemblyPrefix.Length..].ToLowerInvariant();
        var template = action.AttributeRouteInfo?.Template?.TrimStart('~', '/');
        var actionName = $"{controllerType.FullName}.{action.ActionName}";

        if (template is null)
        {
            return $"{actionName} não declara rota por atributo.";
        }

        var isAdminRoute = template.StartsWith($"api/admin/{module}/", StringComparison.OrdinalIgnoreCase);
        var isCustomerRoute = template.StartsWith($"api/{module}/", StringComparison.OrdinalIgnoreCase);
        if (!isAdminRoute && !isCustomerRoute)
        {
            return $"{actionName} usa a rota '{template}', fora de api/{module}/ e api/admin/{module}/.";
        }

        var adminBase = controllerType.Assembly.GetType($"{moduleAssemblyName}.Controllers.Admin.AdminControllerBase");
        var inheritsAdminBase = adminBase is not null && controllerType.IsSubclassOf(adminBase);
        if (isAdminRoute != inheritsAdminBase)
        {
            return isAdminRoute
                ? $"{actionName} usa a rota de backoffice '{template}' sem herdar AdminControllerBase."
                : $"{actionName} herda AdminControllerBase mas usa a rota do cliente '{template}'.";
        }

        return null;
    }
}
