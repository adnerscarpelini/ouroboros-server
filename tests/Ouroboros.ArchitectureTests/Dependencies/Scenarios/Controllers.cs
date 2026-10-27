using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Contracts;
using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Domain;
using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Infrastructure;

namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.Host;

// Fixtures de IL real; não são controllers de src nem endpoints de negócio.
[ApiController]
[Route("__architecture/[controller]")]
public abstract class ScenarioControllerBase : ControllerBase;

public sealed class SignatureController(OrdersStore store) : ScenarioControllerBase
{
    public int Count() => store.Count();
}

public sealed class BodyController : ScenarioControllerBase
{
    public int Count() => new OrdersStore().Count();
}

public sealed class AsyncController : ScenarioControllerBase
{
    public async Task<int> CountAsync()
    {
        await Task.Yield();
        return new OrdersStore().Count();
    }
}

public sealed class ClosureController : ScenarioControllerBase
{
    public int Count()
    {
        Func<int> count = () => new OrdersStore().Count();
        return count();
    }
}

public sealed class DomainController : ScenarioControllerBase
{
    public Order Create() => new();
}

public sealed class ApplicationController : ScenarioControllerBase
{
    public int Count() => Orders.Application.OrderService.Count();
}

public sealed class EfController : ScenarioControllerBase
{
    public Type Model() => typeof(DbContext);
}

public sealed class NpgsqlController : ScenarioControllerBase
{
    public Type Connection() => typeof(NpgsqlConnection);
}

public sealed class AdoController : ScenarioControllerBase
{
    public int Count() => new DataTable().Rows.Count;
}

public abstract class PersistenceControllerBase : ScenarioControllerBase
{
    public int Count() => new OrdersStore().Count();
}

public sealed class InheritedController : PersistenceControllerBase;

public sealed class AllowedController(IOrderQuery query) : ScenarioControllerBase
{
    public IActionResult Read() => Ok(query.Id);
}

[Controller]
[ApiController]
[Route("__architecture/attributed")]
public sealed class AttributedEndpoint
{
    public int Count() => new OrdersStore().Count();
}

[NonController]
public sealed class CompositionController
{
    public OrdersStore Create() => new();
}

public static class CompositionRoot
{
    public static OrdersStore Create() => new();
}
