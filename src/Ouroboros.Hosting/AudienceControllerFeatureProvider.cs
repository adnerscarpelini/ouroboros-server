using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Ouroboros.Hosting;

// O provider padrão só aceita controllers public. Os controllers dos módulos são internal para não
// exporem os casos de uso, então aceitamos qualquer ControllerBase concreto das application parts e
// filtramos pelo público do host: o backoffice registra só os que herdam a AdminControllerBase de
// um módulo (Controllers.Admin); a API externa registra todos os demais.
internal sealed class AudienceControllerFeatureProvider(HostAudience audience) : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo typeInfo) =>
        typeInfo.IsClass
        && !typeInfo.IsAbstract
        && !typeInfo.ContainsGenericParameters
        && !typeInfo.IsDefined(typeof(NonControllerAttribute))
        && typeof(ControllerBase).IsAssignableFrom(typeInfo)
        && IsBackofficeController(typeInfo) == (audience == HostAudience.Backoffice);

    internal static bool IsBackofficeController(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name == "AdminControllerBase"
                && current.Namespace?.EndsWith(".Controllers.Admin", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        return false;
    }
}
