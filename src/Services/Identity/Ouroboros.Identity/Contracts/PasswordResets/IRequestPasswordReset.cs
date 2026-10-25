namespace Ouroboros.Identity.Contracts.PasswordResets;

// Caso de uso: pedir o link de redefinição de senha. O resultado é o mesmo exista ou não uma conta com o
// e-mail, para não revelar quais contas existem; só um formato inválido é informado.
public interface IRequestPasswordReset
{
    Task<RequestPasswordResetResult> ExecuteAsync(RequestPasswordResetRequest request, CancellationToken cancellationToken);
}

public sealed record RequestPasswordResetRequest(string Email);

public enum RequestPasswordResetResult
{
    // Aceita, com ou sem envio do link.
    Accepted,

    // Formato fora da regra de e-mail das contas.
    InvalidEmail,
}
