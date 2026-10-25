namespace Ouroboros.ArchitectureTests.Dependencies;

// Violação legada já inventariada e a issue que a remove. Cada linha é nominal: tipo (ou projeto), dependência
// proibida e entrega responsável. Não vale para namespaces, nem para um tipo inteiro.
internal readonly record struct KnownViolation(string Rule, string Subject, string Dependency, string Issue)
{
    public Violation Violation => new(Rule, Subject, Dependency);
}

// Lista de transição (inventário em docs/implementation-status.md). Não autoriza novas ocorrências: um uso novo, mesmo em
// um tipo listado, falha se a dependência não estiver nomeada aqui, e a remoção de uma dependência exige apagar a linha
// (KnownViolations.Compare acusa as obsoletas). A refatoração de cada issue deixa a lista menor.
internal static class KnownViolations
{
    private const string ApplicationLayer = "layer.application";
    private const string CrossServiceType = "type.cross-service";
    private const string CrossServiceProject = "project.cross-service";
    private const string ForeignServiceExecutable = "project.executable-foreign-service";

    private const string IdentityApplication = "Ouroboros.Modules.Identity.Application.";
    private const string IdentityInfrastructure = "Ouroboros.Modules.Identity.Infrastructure.";

    private const string EntityFramework = "[assembly] Microsoft.EntityFrameworkCore";
    private const string EntityFrameworkRelational = "[assembly] Microsoft.EntityFrameworkCore.Relational";

    public static IReadOnlyList<KnownViolation> All { get; } =
    [
        // #74: persistência de sessões e locks por portas.
        .. Group("#74", ApplicationLayer, IdentityApplication + "Sessions.AccountLockout", IdentityInfrastructure + "Persistence.IdentityDbContext", EntityFramework, EntityFrameworkRelational),
        .. Group("#74", ApplicationLayer, IdentityApplication + "Sessions.Login", IdentityInfrastructure + "Persistence.IdentityDbContext", EntityFramework),
        .. Group("#74", ApplicationLayer, IdentityApplication + "Sessions.Logout", IdentityInfrastructure + "Persistence.IdentityDbContext", EntityFramework),
        .. Group("#74", ApplicationLayer, IdentityApplication + "Sessions.LogoutAll", IdentityInfrastructure + "Persistence.IdentityDbContext", EntityFramework),
        .. Group("#74", ApplicationLayer, IdentityApplication + "Sessions.RefreshSession", IdentityInfrastructure + "Persistence.IdentityDbContext", EntityFramework),

        // #75: persistência de cadastro.
        .. Group("#75", ApplicationLayer, IdentityApplication + "Registrations.CompleteRegistration", IdentityInfrastructure + "Persistence.IdentityDbContext", IdentityInfrastructure + "Persistence.PersistenceErrors", EntityFramework),
        .. Group("#75", ApplicationLayer, IdentityApplication + "Registrations.RequestRegistration", IdentityInfrastructure + "Persistence.IdentityDbContext", EntityFramework, EntityFrameworkRelational),

        // #76: persistência de redefinição de senha.
        .. Group("#76", ApplicationLayer, IdentityApplication + "PasswordResets.CompletePasswordReset", IdentityInfrastructure + "Persistence.IdentityDbContext", EntityFramework),
        .. Group("#76", ApplicationLayer, IdentityApplication + "PasswordResets.RequestPasswordReset", IdentityInfrastructure + "Persistence.IdentityDbContext", EntityFramework, EntityFrameworkRelational),

        // #77: primeiro Admin.
        .. Group("#77", ApplicationLayer, IdentityApplication + "Administrators.CreateFirstAdmin", IdentityInfrastructure + "Persistence.IdentityDbContext", EntityFramework, EntityFrameworkRelational),

        // #78: projeção do usuário autenticado.
        .. Group("#78", ApplicationLayer, IdentityApplication + "Users.GetCurrentUser", IdentityInfrastructure + "Persistence.IdentityDbContext", EntityFramework),

        // #73 e #81: Identity lê parâmetros de Settings; passam a ser locais ao serviço.
        .. Group("#73, #81", CrossServiceProject, "Ouroboros.Modules.Identity", "Ouroboros.Modules.Settings"),
        .. Group("#73, #81", CrossServiceType, IdentityApplication + "Administrators.CreateFirstAdmin", "Ouroboros.Modules.Settings.Contracts.ISystemParameters", "Ouroboros.Modules.Settings.Contracts.ParameterNotFoundException"),
        .. Group("#73, #81", CrossServiceType, IdentityApplication + "PasswordResets.RequestPasswordReset", "Ouroboros.Modules.Settings.Contracts.ISystemParameters"),
        .. Group("#73, #81", CrossServiceType, IdentityApplication + "Registrations.RequestRegistration", "Ouroboros.Modules.Settings.Contracts.ISystemParameters"),
        .. Group("#73, #81", CrossServiceType, IdentityApplication + "Sessions.AccountLockout", "Ouroboros.Modules.Settings.Contracts.ISystemParameters"),

        // #95 e #96: hosts agregadores que compõem todos os módulos.
        .. Group("#95, #96", ForeignServiceExecutable, "Ouroboros.Api", "Ouroboros.Modules.Accounts", "Ouroboros.Modules.Customers", "Ouroboros.Modules.Identity", "Ouroboros.Modules.Settings", "Ouroboros.Modules.Transactions"),
        .. Group("#95, #96", ForeignServiceExecutable, "Ouroboros.Backoffice.Api", "Ouroboros.Modules.Accounts", "Ouroboros.Modules.Customers", "Ouroboros.Modules.Identity", "Ouroboros.Modules.Settings", "Ouroboros.Modules.Transactions"),
    ];

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

    private static IEnumerable<KnownViolation> Group(string issue, string rule, string subject, params string[] dependencies) =>
        dependencies.Select(dependency => new KnownViolation(rule, subject, dependency, issue));
}
