using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Host;
using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Application;
using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Contracts;
using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Domain;
using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Infrastructure;
using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Payments.Application;
using Ouroboros.ArchitectureTests.Dependencies.Scenarios.Shared;

namespace Ouroboros.ArchitectureTests.Dependencies;

// Prova que as regras detectam o que dizem detectar, com IL real (Scenarios/Orders.cs) e grafos controlados, e não
// apenas que o código atual as satisfaz.
public sealed class DependencyRuleScenarioTests
{
    private const string ScenarioRoot = "Ouroboros.ArchitectureTests.Dependencies.Scenarios";

    private static readonly IReadOnlyList<Violation> ScenarioViolations = DependencyRules.CheckTypes(
        AssemblyScanner.Scan(typeof(DependencyRuleScenarioTests).Assembly.Location).Dependencies,
        new ScenarioLocator());

    public static TheoryData<Type, string, string> ForbiddenUses => new()
    {
        { typeof(BodyOnlyViolation), "layer.application", typeof(Clock).FullName! },
        { typeof(LocalVariableViolation), "layer.application", typeof(OrdersStore).FullName! },
        { typeof(CatchViolation), "layer.application", typeof(StoreException).FullName! },
        { typeof(GenericArgumentViolation), "layer.application", typeof(OrdersStore).FullName! },
        { typeof(BaseTypeViolation), "layer.application", typeof(OrdersStore).FullName! },
        { typeof(DataAccessViolation), "layer.application", "[assembly] System.Data.Common" },
        { typeof(DomainUsesApplication), "layer.domain", typeof(OrderService).FullName! },
        { typeof(DomainUsesHttp), "layer.domain", "[assembly] System.Net.Http" },
        { typeof(ContractsUsesDomain), "layer.contracts", typeof(Order).FullName! },
        { typeof(CallsPayments), "type.cross-service", typeof(PaymentsService).FullName! },
        { typeof(CallsOrders), "type.cross-service", typeof(OrderService).FullName! },
        { typeof(CallsHost), "type.depends-on-executable", typeof(HostEntry).FullName! },
        { typeof(SharedUsesService), "type.shared-depends-on-service", typeof(OrderService).FullName! },
    };

    [Theory]
    [MemberData(nameof(ForbiddenUses))]
    public void Forbidden_dependency_is_reported_with_the_type_and_the_dependency(Type owner, string rule, string dependency)
    {
        Assert.Contains(new Violation(rule, owner.FullName!, dependency), ScenarioViolations);
    }

    // A violação não pode vir de nenhuma assinatura: o único rastro de Clock está no IL do método.
    [Fact]
    public void Dependency_visible_only_in_the_method_body_is_found()
    {
        Assert.Equal(
            [new Violation("layer.application", typeof(BodyOnlyViolation).FullName!, typeof(Clock).FullName!)],
            ScenarioViolations.Where(violation => violation.Subject == typeof(BodyOnlyViolation).FullName));
    }

    [Theory]
    [InlineData(typeof(DomainAllowed))]
    [InlineData(typeof(ApplicationAllowed))]
    [InlineData(typeof(InfrastructureAllowed))]
    public void Allowed_dependencies_pass(Type owner)
    {
        Assert.DoesNotContain(ScenarioViolations, violation => violation.Subject == owner.FullName);
    }

    [Fact]
    public void Only_scenario_types_are_evaluated()
    {
        Assert.All(ScenarioViolations.Where(violation => !violation.Rule.EndsWith("cycle", StringComparison.Ordinal)), violation => Assert.StartsWith(ScenarioRoot, violation.Subject, StringComparison.Ordinal));
    }

    [Fact]
    public void Cross_service_use_in_both_directions_is_a_cycle()
    {
        Assert.Contains(new Violation("type.service-cycle", "Orders, Payments", "ciclo entre serviços"), ScenarioViolations);
    }

    [Fact]
    public void Cycles_between_modules_are_found_once_each()
    {
        var cycles = DependencyRules.Cycles(
            [("A", "B"), ("B", "C"), ("C", "A"), ("C", "D"), ("D", "E"), ("E", "D"), ("F", "G"), ("H", "H")]);

        Assert.Equal(["A, B, C", "D, E", "H"], cycles);
    }

    [Fact]
    public void Acyclic_dependencies_are_not_reported()
    {
        Assert.Empty(DependencyRules.Cycles([("Identity", "Settings"), ("Notifications", "Settings"), ("Identity", "Notifications")]));
    }

    [Fact]
    public void Project_graph_cycle_is_reported()
    {
        var violations = DependencyRules.CheckProjects(
        [
            Project("Ouroboros.Orders", ProjectKind.Service, "Orders", "Ouroboros.Payments"),
            Project("Ouroboros.Payments", ProjectKind.Service, "Payments", "Ouroboros.Orders"),
        ]);

        Assert.Contains(new Violation("project.cycle", "Ouroboros.Orders, Ouroboros.Payments", "ciclo entre projetos"), violations);
        Assert.Contains(new Violation("project.service-cycle", "Orders, Payments", "ciclo entre serviços"), violations);
        Assert.Contains(new Violation("project.cross-service", "Ouroboros.Orders", "Ouroboros.Payments"), violations);
    }

    [Fact]
    public void Shared_code_cannot_reference_services_or_hosts()
    {
        var violations = DependencyRules.CheckProjects(
        [
            Project(DependencyRules.BuildingBlocks, ProjectKind.Shared, null, "Ouroboros.Orders.Api", "Ouroboros.Hosting"),
            Project("Ouroboros.Hosting", ProjectKind.Shared, null, "Ouroboros.Orders"),
            Project("Ouroboros.Orders", ProjectKind.Service, "Orders", DependencyRules.BuildingBlocks),
            Project("Ouroboros.Orders.Api", ProjectKind.Executable, "Orders", "Ouroboros.Orders", DependencyRules.BuildingBlocks, "Ouroboros.Hosting"),
        ]);

        Assert.Equal(
            [
                new Violation("project.building-blocks-leaf", DependencyRules.BuildingBlocks, "Ouroboros.Hosting"),
                new Violation("project.building-blocks-leaf", DependencyRules.BuildingBlocks, "Ouroboros.Orders.Api"),
                new Violation("project.shared-depends-on-service", DependencyRules.BuildingBlocks, "Ouroboros.Orders.Api"),
                new Violation("project.shared-depends-on-service", "Ouroboros.Hosting", "Ouroboros.Orders"),
            ],
            violations.Where(violation => violation.Rule != "project.cycle"));
    }

    [Fact]
    public void Executable_composes_only_its_own_service()
    {
        var violations = DependencyRules.CheckProjects(
        [
            Project("Ouroboros.Orders", ProjectKind.Service, "Orders"),
            Project("Ouroboros.Payments", ProjectKind.Service, "Payments"),
            Project("Ouroboros.Orders.Api", ProjectKind.Executable, "Orders", "Ouroboros.Orders", "Ouroboros.Payments"),
            Project("Ouroboros.Host", ProjectKind.Executable, null, "Ouroboros.Orders.Api"),
            Project("Ouroboros.Orders.Core", ProjectKind.Service, "Orders", "Ouroboros.Orders", "Ouroboros.Orders.Api"),
        ]);

        Assert.Equal(
            [
                new Violation("project.executable-depends-on-executable", "Ouroboros.Host", "Ouroboros.Orders.Api"),
                new Violation("project.executable-foreign-service", "Ouroboros.Orders.Api", "Ouroboros.Payments"),
                new Violation("project.service-depends-on-executable", "Ouroboros.Orders.Core", "Ouroboros.Orders.Api"),
            ],
            violations.Where(violation => violation.Rule != "project.cycle"));
    }

    [Fact]
    public void Same_service_projects_may_reference_each_other()
    {
        Assert.Empty(DependencyRules.CheckProjects(
        [
            Project(DependencyRules.BuildingBlocks, ProjectKind.Shared, null),
            Project("Ouroboros.Orders", ProjectKind.Service, "Orders", DependencyRules.BuildingBlocks),
            Project("Ouroboros.Orders.Api", ProjectKind.Executable, "Orders", "Ouroboros.Orders", DependencyRules.BuildingBlocks),
        ]));
    }

    [Fact]
    public void A_violation_outside_the_transition_list_is_new()
    {
        var known = new KnownViolation("layer.application", "Orders.Placed", "Orders.Store", "#1");
        var (added, obsolete) = KnownViolations.Compare(
            [known.Violation, new Violation("layer.application", "Orders.Placed", "Orders.Cache")],
            [known]);

        Assert.Equal([new Violation("layer.application", "Orders.Placed", "Orders.Cache")], added);
        Assert.Empty(obsolete);
    }

    [Fact]
    public void An_exception_whose_dependency_was_removed_is_obsolete()
    {
        var removed = new KnownViolation("layer.application", "Orders.Placed", "Orders.Store", "#1");
        var (added, obsolete) = KnownViolations.Compare([], [removed]);

        Assert.Empty(added);
        Assert.Equal([removed], obsolete);
    }

    private static ProjectInfo Project(string name, ProjectKind kind, string? service, params string[] references) =>
        new(name, kind, service, references);

    // Os cenários vivem no assembly de testes; cada namespace sob Scenarios é um serviço com as camadas habituais,
    // exceto Host (executável) e Shared (código técnico compartilhado).
    private sealed class ScenarioLocator : ICodeLocator
    {
        public CodeLocation Locate(TypeId type)
        {
            if (!type.Namespace.StartsWith(ScenarioRoot + ".", StringComparison.Ordinal))
            {
                return new CodeLocation(CodeKind.External, null, null, type.Assembly);
            }

            var service = type.Namespace[(ScenarioRoot.Length + 1)..].Split('.')[0];
            if (service is "Host" or "Shared")
            {
                return new CodeLocation(service == "Host" ? CodeKind.Executable : CodeKind.Shared, null, null, type.Assembly);
            }

            return new CodeLocation(CodeKind.Service, service, DependencyRules.LayerOf(type.Namespace, $"{ScenarioRoot}.{service}", sharedCode: false), type.Assembly);
        }
    }
}
