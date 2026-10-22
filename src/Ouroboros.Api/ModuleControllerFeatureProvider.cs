using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Ouroboros.Api;

// O provider padrão só aceita controllers public. Os controllers dos módulos são internal para
// não exporem os casos de uso, então aceitamos qualquer ControllerBase concreto das application
// parts registradas pelos módulos.
internal sealed class ModuleControllerFeatureProvider : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo typeInfo) =>
        typeInfo.IsClass
        && !typeInfo.IsAbstract
        && !typeInfo.ContainsGenericParameters
        && !typeInfo.IsDefined(typeof(NonControllerAttribute))
        && typeof(ControllerBase).IsAssignableFrom(typeInfo);
}
