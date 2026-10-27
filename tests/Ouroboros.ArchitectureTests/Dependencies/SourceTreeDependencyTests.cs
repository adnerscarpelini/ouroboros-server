using System.Text.RegularExpressions;

namespace Ouroboros.ArchitectureTests.Dependencies;

// Aplica a matriz de docs/architecture/layer-dependencies.md ao código do checkout. Os projetos e assemblies vêm da árvore
// de src, então serviços, APIs e workers novos entram nas regras sem alterar estes testes. Violações legadas só passam
// se estiverem nomeadas em KnownViolations.
public sealed partial class SourceTreeDependencyTests
{
    private static readonly SourceTree Tree = SourceTree.Current;

    [Fact]
    public void Projects_are_discovered_from_the_source_tree()
    {
        Assert.NotEmpty(Tree.Services);
        Assert.Contains(Tree.Projects.Projects, project => project.Kind == ProjectKind.Executable);
        Assert.Contains(Tree.Projects.Projects, project => project.Name == DependencyRules.BuildingBlocks);
    }

    [Fact]
    public void Every_project_has_a_compiled_assembly_to_analyze()
    {
        Assert.Empty(Tree.MissingAssemblies());
    }

    // Garante que as regras de camada não passam por não encontrarem nada: cada camada tem código classificado.
    [Theory]
    [InlineData(nameof(Layer.Domain))]
    [InlineData(nameof(Layer.Contracts))]
    [InlineData(nameof(Layer.Application))]
    [InlineData(nameof(Layer.Infrastructure))]
    public void Layer_rules_apply_to_existing_code(string layerName)
    {
        var layer = Enum.Parse<Layer>(layerName);
        var types = Tree.Scans.SelectMany(scan => scan.Types).Select(type => type.Id);

        Assert.Contains(types, type => Tree.Locate(type).Layer == layer);
    }

    // A base técnica de domínio (Entity) segue a regra de Domain: sem EF nem tipos de persistência, mesmo no mesmo assembly.
    [Fact]
    public void Shared_domain_base_is_subject_to_the_domain_rule()
    {
        var entity = Tree.Scans.SelectMany(scan => scan.Types).Select(type => type.Id)
            .Single(type => type.FullName == "Ouroboros.BuildingBlocks.Domain.Entity");

        Assert.Equal(new CodeLocation(CodeKind.Shared, null, Layer.Domain, DependencyRules.BuildingBlocks), Tree.Locate(entity));
    }

    [Fact]
    public void Project_references_and_solution_have_no_dangling_entries()
    {
        Assert.Empty(Tree.Projects.DanglingReferences);
        Assert.Empty(Tree.Projects.SolutionProblems);
    }

    [Fact]
    public void Project_references_follow_the_rules()
    {
        AssertMatchesKnown(DependencyRules.CheckProjects(Tree.Projects.Projects), "project.");
    }

    [Fact]
    public void Type_dependencies_follow_the_rules()
    {
        var violations = DependencyRules.CheckTypes(Tree.Scans.SelectMany(scan => scan.Dependencies), Tree);

        AssertMatchesKnown(violations, "layer.", "type.");
    }

    [Fact]
    public void Controllers_use_contracts_without_domain_application_or_persistence()
    {
        var controllers = ControllerTypes.Discover(Tree.Scans.Select(scan => Tree.Load(scan.Name)));
        Assert.NotEmpty(controllers);
        AssertMatchesKnown(DependencyRules.CheckControllers(Tree.Scans.SelectMany(scan => scan.Dependencies), controllers, Tree),
            "type.controller-dependency");
    }

    [Fact]
    public void Known_violations_name_a_removal_issue_once()
    {
        Assert.All(KnownViolations.All, entry => Assert.Matches(IssuePattern(), entry.Issue));
        Assert.Empty(KnownViolations.All.GroupBy(entry => entry.Violation).Where(group => group.Count() > 1).Select(group => group.Key));
    }

    // Falha com a lista de violações novas (corrija o código; não amplie a lista) e com as exceções obsoletas (remova-as).
    private static void AssertMatchesKnown(IReadOnlyList<Violation> actual, params string[] rulePrefixes)
    {
        bool InScope(string rule) => rulePrefixes.Any(prefix => rule.StartsWith(prefix, StringComparison.Ordinal));

        var (added, obsolete) = KnownViolations.Compare(
            actual.Where(violation => InScope(violation.Rule)),
            KnownViolations.All.Where(entry => InScope(entry.Rule)));

        Assert.True(added.Count == 0, "Violações fora da lista de transição:" + Environment.NewLine + string.Join(Environment.NewLine, added));
        Assert.True(
            obsolete.Count == 0,
            "A dependência não existe mais; remova a exceção:" + Environment.NewLine + string.Join(Environment.NewLine, obsolete.Select(entry => $"{entry.Violation} ({entry.Issue})")));
    }

    [GeneratedRegex(@"^#\d+(, #\d+)*$")]
    private static partial Regex IssuePattern();
}
