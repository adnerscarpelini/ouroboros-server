namespace Ouroboros.ArchitectureTests.Dependencies;

// Violação legada já inventariada e a issue que a remove. Cada linha é nominal: tipo (ou projeto), dependência
// proibida e entrega responsável. Não vale para namespaces, nem para um tipo inteiro.
internal readonly record struct KnownViolation(string Rule, string Subject, string Dependency, string Issue)
{
    public Violation Violation => new(Rule, Subject, Dependency);
}

// Lista de transição (inventário em docs/implementation-status.md). Não autoriza novas ocorrências: um uso novo, mesmo em
// um tipo listado, falha se a dependência não estiver nomeada aqui, e a remoção de uma dependência exige apagar a linha
// (KnownViolations.Compare acusa as obsoletas). A lista está vazia; uma exceção nova só entra com uma issue nomeada que a remova.
internal static class KnownViolations
{
    public static IReadOnlyList<KnownViolation> All { get; } = [];

    // Novas: encontradas e não listadas (a regra falha). Obsoletas: listadas e não encontradas (a exceção deve sair).
    public static (IReadOnlyList<Violation> New, IReadOnlyList<KnownViolation> Obsolete) Compare(
        IEnumerable<Violation> actual,
        IEnumerable<KnownViolation> known)
    {
        var found = actual.ToHashSet();
        var listed = known.ToList();
        var listedViolations = listed.Select(entry => entry.Violation).ToHashSet();

        return ([.. found.Except(listedViolations).OrderBy(violation => violation.ToString(), StringComparer.Ordinal)], [.. listed.Where(entry => !found.Contains(entry.Violation))]);
    }
}
