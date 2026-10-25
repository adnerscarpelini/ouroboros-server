namespace Ouroboros.Identity.Contracts.Registrations;

// Caso de uso: concluir o cadastro de cliente com o token recebido por e-mail, definindo a senha. Cria
// um usuário ativo com o papel Customer. Não faz login.
public interface ICompleteRegistration
{
    Task<CompleteRegistrationResult> ExecuteAsync(CompleteRegistrationRequest request, CancellationToken cancellationToken);
}

public sealed record CompleteRegistrationRequest(string? Token, string? Password);

public enum CompleteRegistrationOutcome
{
    Completed,

    // Token vazio, inexistente, expirado, já usado, invalidado ou de outro tipo, ou e-mail que passou a
    // pertencer a outra conta: sempre a mesma resposta.
    InvalidToken,

    // Senha fora da política; o token continua válido.
    PasswordRejected,
}

// Message traz a regra de senha violada quando o resultado é PasswordRejected.
public sealed record CompleteRegistrationResult(CompleteRegistrationOutcome Outcome, string? Message = null);
