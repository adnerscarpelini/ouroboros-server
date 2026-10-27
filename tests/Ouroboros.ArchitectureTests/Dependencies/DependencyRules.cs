namespace Ouroboros.ArchitectureTests.Dependencies;

// Rule é o código estável da regra; Subject, o projeto ou o tipo que depende; Dependency, o que ele não pode usar.
// Para tipos de src, Dependency é o nome completo; para bibliotecas externas, o assembly (ver DependencyRules.Describe).
internal readonly record struct Violation(string Rule, string Subject, string Dependency)
{
    public override string ToString() => $"[{Rule}] {Subject} -> {Dependency}";
}

internal enum Layer
{
    Domain,
    Contracts,
    Application,
    Infrastructure,
}

internal enum CodeKind
{
    Service,
    Executable,
    Shared,
    External,
}

// Onde um tipo vive na arquitetura. Service só existe para código de serviço; Layer, quando o namespace
// segue <raiz>.<Camada>.
internal sealed record CodeLocation(CodeKind Kind, string? Service, Layer? Layer, string Assembly);

internal interface ICodeLocator
{
    CodeLocation Locate(TypeId type);
}

// Matriz de docs/architecture/layer-dependencies.md aplicada a projetos e a usos de tipos.
internal static class DependencyRules
{
    public const string BuildingBlocks = "Ouroboros.BuildingBlocks";

    // Aplicação não conhece persistência, SQL nem HTTP: nada disso atravessa uma porta.
    private static readonly string[] ApplicationForbiddenAssemblies =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "System.Data",
        "System.Net.Http",
        "Microsoft.AspNetCore.Http",
        "Microsoft.AspNetCore.Mvc",
    ];

    // BCL que o domínio não pode usar mesmo estando em System.*.
    private static readonly string[] DomainForbiddenFramework = ["System.Net.Http", "System.Data"];

    public static IReadOnlyList<Violation> CheckProjects(IReadOnlyList<ProjectInfo> projects)
    {
        var byName = projects.ToDictionary(project => project.Name);
        var violations = new List<Violation>();

        foreach (var project in projects)
        {
            foreach (var reference in project.References.Select(name => byName[name]))
            {
                violations.AddRange(CheckReference(project, reference));
            }
        }

        var projectGraph = projects.SelectMany(project => project.References.Select(reference => (project.Name, reference)));
        violations.AddRange(Cycles(projectGraph).Select(cycle => new Violation("project.cycle", cycle, "ciclo entre projetos")));

        var serviceGraph = projects
            .Where(project => project.Kind == ProjectKind.Service)
            .SelectMany(project => project.References.Select(reference => (From: project.Service!, To: byName[reference])))
            .Where(edge => edge.To.Kind == ProjectKind.Service && edge.To.Service != edge.From)
            .Select(edge => (edge.From, edge.To.Service!));
        violations.AddRange(Cycles(serviceGraph).Select(cycle => new Violation("project.service-cycle", cycle, "ciclo entre serviços")));

        return Distinct(violations);
    }

    private static IEnumerable<Violation> CheckReference(ProjectInfo from, ProjectInfo to)
    {
        if (from.Name == BuildingBlocks)
        {
            yield return new Violation("project.building-blocks-leaf", from.Name, to.Name);
        }

        switch (from.Kind, to.Kind)
        {
            case (ProjectKind.Shared, ProjectKind.Service or ProjectKind.Executable):
                yield return new Violation("project.shared-depends-on-service", from.Name, to.Name);
                break;
            case (ProjectKind.Service, ProjectKind.Executable):
                yield return new Violation("project.service-depends-on-executable", from.Name, to.Name);
                break;
            case (ProjectKind.Executable, ProjectKind.Executable):
                yield return new Violation("project.executable-depends-on-executable", from.Name, to.Name);
                break;
            case (ProjectKind.Service, ProjectKind.Service) when from.Service != to.Service:
                yield return new Violation("project.cross-service", from.Name, to.Name);
                break;
            case (ProjectKind.Executable, ProjectKind.Service) when from.Service != to.Service:
                yield return new Violation("project.executable-foreign-service", from.Name, to.Name);
                break;
        }
    }

    public static IReadOnlyList<Violation> CheckTypes(IEnumerable<TypeDependency> dependencies, ICodeLocator locator)
    {
        var violations = new List<Violation>();
        var serviceEdges = new HashSet<(string, string)>();

        foreach (var (owner, target) in dependencies)
        {
            var from = locator.Locate(owner);
            var to = locator.Locate(target);
            if (from.Kind == CodeKind.External)
            {
                continue;
            }

            if (from.Service is not null && to.Service is not null && from.Service != to.Service)
            {
                violations.Add(new Violation("type.cross-service", owner.FullName, Describe(target, to)));
                serviceEdges.Add((from.Service, to.Service));
            }

            violations.AddRange(CheckTypeUse(owner, from, target, to));
        }

        violations.AddRange(Cycles(serviceEdges).Select(cycle => new Violation("type.service-cycle", cycle, "ciclo entre serviços")));
        return Distinct(violations);
    }

    public static IReadOnlyList<Violation> CheckControllers(
        IEnumerable<TypeDependency> dependencies, IReadOnlySet<TypeId> controllers, ICodeLocator locator)
    {
        var violations = new List<Violation>();
        foreach (var (owner, target) in dependencies.Where(dependency => controllers.Contains(dependency.Owner)))
        {
            var location = locator.Locate(target);
            if (location.Layer is Layer.Domain or Layer.Application or Layer.Infrastructure
                || (location.Kind == CodeKind.External && new[] { "Microsoft.EntityFrameworkCore", "Npgsql", "System.Data" }
                    .Any(assembly => IsAssembly(location.Assembly, assembly))))
            {
                violations.Add(new Violation("type.controller-dependency", owner.FullName, Describe(target, location)));
            }
        }
        return Distinct(violations);
    }

    private static IEnumerable<Violation> CheckTypeUse(TypeId owner, CodeLocation from, TypeId target, CodeLocation to)
    {
        var dependency = Describe(target, to);

        if (from.Kind is CodeKind.Service or CodeKind.Shared)
        {
            if (to.Kind == CodeKind.Executable)
            {
                yield return new Violation("type.depends-on-executable", owner.FullName, dependency);
            }
            else if (from.Kind == CodeKind.Shared && to.Kind == CodeKind.Service)
            {
                yield return new Violation("type.shared-depends-on-service", owner.FullName, dependency);
            }
        }

        switch (from.Layer)
        {
            case Layer.Domain when !AllowedInDomain(from, to):
                yield return new Violation("layer.domain", owner.FullName, dependency);
                break;
            case Layer.Contracts when !AllowedInContracts(from, to):
                yield return new Violation("layer.contracts", owner.FullName, dependency);
                break;
            case Layer.Application when ForbiddenInApplication(to):
                yield return new Violation("layer.application", owner.FullName, dependency);
                break;
        }
    }

    // Domain: BCL, a base técnica de domínio e o próprio Domain. Não vê Contracts, Application, Infrastructure nem outro serviço.
    private static bool AllowedInDomain(CodeLocation from, CodeLocation to) => to.Kind switch
    {
        CodeKind.External => IsFramework(to.Assembly) && !DomainForbiddenFramework.Any(forbidden => IsAssembly(to.Assembly, forbidden)),
        CodeKind.Shared => to.Layer == Layer.Domain,
        _ => to.Layer == Layer.Domain && to.Service == from.Service,
    };

    // Contracts: BCL, dados simples e os próprios contratos. Sem entidades, infraestrutura nem contratos de outro serviço.
    private static bool AllowedInContracts(CodeLocation from, CodeLocation to) => to.Kind switch
    {
        CodeKind.External => IsFramework(to.Assembly),
        CodeKind.Shared => to.Layer == Layer.Contracts,
        _ => to.Layer == Layer.Contracts && to.Service == from.Service,
    };

    // Application: sem implementação de Infrastructure (nem a técnica compartilhada) e sem EF, SQL ou HTTP.
    private static bool ForbiddenInApplication(CodeLocation to) => to.Kind == CodeKind.External
        ? ApplicationForbiddenAssemblies.Any(forbidden => IsAssembly(to.Assembly, forbidden))
        : to.Layer == Layer.Infrastructure;

    public static bool IsFramework(string assembly) =>
        assembly is "mscorlib" or "netstandard" or "System" || assembly.StartsWith("System.", StringComparison.Ordinal);

    private static bool IsAssembly(string assembly, string prefix) =>
        assembly == prefix || assembly.StartsWith(prefix + ".", StringComparison.Ordinal);

    // Tipos de src são nominais; para uma biblioteca externa a dependência proibida é o assembly (EF Core, Npgsql...).
    private static string Describe(TypeId target, CodeLocation location) =>
        location.Kind == CodeKind.External ? $"[assembly] {location.Assembly}" : target.FullName;

    public static Layer? LayerOf(string @namespace, string root, bool sharedCode)
    {
        if (!@namespace.StartsWith(root + ".", StringComparison.Ordinal))
        {
            return null;
        }

        var segment = @namespace[(root.Length + 1)..].Split('.')[0];
        return segment switch
        {
            "Domain" => Layer.Domain,
            "Contracts" => Layer.Contracts,
            "Application" => Layer.Application,
            "Infrastructure" => Layer.Infrastructure,
            // Persistência técnica compartilhada (EF) é infraestrutura mesmo sem se chamar assim.
            "Persistence" when sharedCode => Layer.Infrastructure,
            _ => null,
        };
    }

    // Componentes fortemente conectados com mais de um nó (ou laço), cada um descrito uma vez, em ordem estável.
    public static IReadOnlyList<string> Cycles(IEnumerable<(string From, string To)> edges)
    {
        var graph = edges.GroupBy(edge => edge.From).ToDictionary(group => group.Key, group => group.Select(edge => edge.To).Distinct().ToList());
        var nodes = graph.Keys.Concat(graph.Values.SelectMany(targets => targets)).Distinct().Order(StringComparer.Ordinal).ToList();

        var index = 0;
        var indexes = new Dictionary<string, int>();
        var lowLinks = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var cycles = new List<string>();

        void Visit(string node)
        {
            indexes[node] = lowLinks[node] = index++;
            stack.Push(node);
            onStack.Add(node);

            foreach (var next in graph.GetValueOrDefault(node) ?? [])
            {
                if (!indexes.ContainsKey(next))
                {
                    Visit(next);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[next]);
                }
                else if (onStack.Contains(next))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indexes[next]);
                }
            }

            if (lowLinks[node] != indexes[node])
            {
                return;
            }

            var component = new List<string>();
            string member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            }
            while (member != node);

            if (component.Count > 1 || graph.GetValueOrDefault(node)?.Contains(node) == true)
            {
                cycles.Add(string.Join(", ", component.Order(StringComparer.Ordinal)));
            }
        }

        foreach (var node in nodes.Where(node => !indexes.ContainsKey(node)))
        {
            Visit(node);
        }

        return [.. cycles.Order(StringComparer.Ordinal)];
    }

    private static IReadOnlyList<Violation> Distinct(IEnumerable<Violation> violations) =>
        [.. violations.Distinct().OrderBy(violation => violation.Rule, StringComparer.Ordinal).ThenBy(violation => violation.Subject, StringComparer.Ordinal).ThenBy(violation => violation.Dependency, StringComparer.Ordinal)];
}
