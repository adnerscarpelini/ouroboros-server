using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.Administrators;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Administrators;

internal sealed class CreateFirstAdmin(
    IFirstAdminStore store,
    IPasswordHasher passwordHasher,
    ISystemParameters parameters) : ICreateFirstAdmin
{
    public const string AllowedEmailDomainParameter = "Identity:AdminInvitations:AllowedEmailDomain";

    public async Task<CreateFirstAdminResult> ExecuteAsync(CreateFirstAdminRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !IsEmail(User.NormalizeEmail(request.Email), out var email, out var host))
        {
            return new(CreateFirstAdminOutcome.InvalidEmail, "Informe um e-mail válido.");
        }

        string allowedDomain;
        try
        {
            // O Identity já valida o domínio em minúsculas; a normalização aqui protege a comparação.
            allowedDomain = (await parameters.GetAsync(AllowedEmailDomainParameter, cancellationToken)).Trim().ToLowerInvariant();
        }
        catch (ParameterNotFoundException)
        {
            return new(
                CreateFirstAdminOutcome.ConfigurationMissing,
                $"O parâmetro do sistema '{AllowedEmailDomainParameter}' não existe; aplique as migrations do Identity.");
        }

        if (!string.Equals(host, allowedDomain, StringComparison.Ordinal))
        {
            return new(
                CreateFirstAdminOutcome.EmailDomainNotAllowed,
                $"O e-mail de administrador precisa ser do domínio '{allowedDomain}' (subdomínios não são aceitos).");
        }

        if (PasswordPolicy.Validate(request.Password, email) is { } rule)
        {
            return new(CreateFirstAdminOutcome.PasswordRejected, rule);
        }

        // Hash fora da transação de persistência; a porta serializa a verificação e a criação.
        var admin = User.Create(email, passwordHasher.Hash(request.Password), UserRole.Admin);
        var outcome = await store.TryCreateAsync(admin, cancellationToken);
        return outcome switch
        {
            FirstAdminCreationOutcome.Created => new(CreateFirstAdminOutcome.Created, $"Administrador {email} criado.", admin.ExternalId),
            FirstAdminCreationOutcome.AdminAlreadyExists => new(
                CreateFirstAdminOutcome.AdminAlreadyExists,
                "Já existe um administrador. Novos administradores entram por convite."),
            FirstAdminCreationOutcome.EmailAlreadyInUse => new(CreateFirstAdminOutcome.EmailAlreadyInUse, "Esse e-mail já pertence a outro usuário."),
            _ => throw new InvalidOperationException("Resultado de persistência do primeiro administrador desconhecido."),
        };
    }

    private static bool IsEmail(string normalized, out string email, out string host)
    {
        email = normalized;
        host = "";
        if (!EmailAddresses.IsValid(normalized))
        {
            return false;
        }

        host = normalized[(normalized.LastIndexOf('@') + 1)..];
        return true;
    }
}
