namespace Ouroboros.Identity.Contracts.Administrators;

// Caso de uso: criar o primeiro administrador do sistema (usado pelo comando create-admin do host
// de backoffice). Só funciona enquanto não existir nenhum Admin.
public interface ICreateFirstAdmin
{
    Task<CreateFirstAdminResult> ExecuteAsync(CreateFirstAdminRequest request, CancellationToken cancellationToken);
}

public sealed record CreateFirstAdminRequest(string Email, string Password);

public enum CreateFirstAdminOutcome
{
    Created,
    AdminAlreadyExists,
    InvalidEmail,
    EmailDomainNotAllowed,
    EmailAlreadyInUse,
    PasswordRejected,

    // Parâmetro do sistema necessário (domínio aceito) não existe.
    ConfigurationMissing,
}

// Message explica a falha sem nunca repetir a senha. AdminId é o ExternalId do Admin criado.
public sealed record CreateFirstAdminResult(CreateFirstAdminOutcome Outcome, string Message, Guid? AdminId = null)
{
    public bool Succeeded => Outcome == CreateFirstAdminOutcome.Created;
}
