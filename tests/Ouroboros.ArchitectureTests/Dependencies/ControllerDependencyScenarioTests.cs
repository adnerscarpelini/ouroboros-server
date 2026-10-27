using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Host;
using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Infrastructure;

namespace Ouroboros.ArchitectureTests.Dependencies;

public sealed class ControllerDependencyScenarioTests
{
    private static readonly AssemblyScan Scan = AssemblyScanner.Scan(typeof(ControllerDependencyScenarioTests).Assembly.Location);
    private static readonly IReadOnlySet<TypeId> Controllers = ControllerTypes.Discover([typeof(ControllerDependencyScenarioTests).Assembly]);
    private static readonly IReadOnlyList<Violation> Violations = DependencyRules.CheckControllers(Scan.Dependencies, Controllers,
        new DependencyRuleScenarioTests.ScenarioLocator());

    public static TheoryData<Type, string> ForbiddenUses => new()
    {
        { typeof(SignatureController), typeof(OrdersStore).FullName! },
        { typeof(BodyController), typeof(OrdersStore).FullName! },
        { typeof(AsyncController), typeof(OrdersStore).FullName! },
        { typeof(ClosureController), typeof(OrdersStore).FullName! },
        { typeof(DomainController), typeof(Scenarios.Orders.Domain.Order).FullName! },
        { typeof(ApplicationController), typeof(Scenarios.Orders.Application.OrderService).FullName! },
        { typeof(EfController), "[assembly] Microsoft.EntityFrameworkCore" },
        { typeof(NpgsqlController), "[assembly] Npgsql" },
        { typeof(AdoController), "[assembly] System.Data.Common" },
        { typeof(PersistenceControllerBase), typeof(OrdersStore).FullName! },
        { typeof(AttributedEndpoint), typeof(OrdersStore).FullName! },
    };

    [Theory]
    [MemberData(nameof(ForbiddenUses))]
    public void Controller_implementation_dependencies_are_reported(Type controller, string dependency) =>
        Assert.Contains(new Violation("type.controller-dependency", controller.FullName!, dependency), Violations);

    [Fact]
    public void Contracts_http_and_composition_remain_allowed()
    {
        Assert.Contains(ControllerTypes.Id(typeof(AllowedController)), Controllers);
        Assert.Contains(ControllerTypes.Id(typeof(InheritedController)), Controllers);
        Assert.DoesNotContain(ControllerTypes.Id(typeof(CompositionController)), Controllers);
        Assert.DoesNotContain(Violations, violation => violation.Subject == typeof(AllowedController).FullName
            || violation.Subject == typeof(CompositionController).FullName || violation.Subject == typeof(CompositionRoot).FullName);
    }
}
