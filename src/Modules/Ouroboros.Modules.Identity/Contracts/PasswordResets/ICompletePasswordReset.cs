namespace Ouroboros.Modules.Identity.Contracts.PasswordResets;

// Caso de uso: definir uma senha nova com o token recebido por e-mail. Também remove o bloqueio por
// falhas e encerra todas as sessões do usuário. Não faz login.
public interface ICompletePasswordReset
{
    Task<CompletePasswordResetResult> ExecuteAsync(CompletePasswordResetRequest request, CancellationToken cancellationToken);
}

public sealed record CompletePasswordResetRequest(string? Token, string? Password);

public enum CompletePasswordResetOutcome
{
    Completed,

    // Token vazio, inexistente, expirado, já usado, invalidado, de outro tipo ou de usuário inativo:
    // sempre a mesma resposta.
    InvalidToken,

    // Senha fora da política ou igual à atual; o token continua válido.
    PasswordRejected,
}

// Message traz a regra de senha violada quando o resultado é PasswordRejected.
public sealed record CompletePasswordResetResult(CompletePasswordResetOutcome Outcome, string? Message = null);
