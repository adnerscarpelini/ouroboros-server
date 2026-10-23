namespace Ouroboros.Modules.Identity.Contracts.Sessions;

// Caso de uso: encerrar a sessão de um refresh token. Quem tem o token pode encerrar a sessão dele,
// sem access token. É idempotente e não afeta as outras sessões do usuário.
public interface ILogout
{
    Task<LogoutResult> ExecuteAsync(LogoutRequest request, CancellationToken cancellationToken);
}

public sealed record LogoutRequest(string? RefreshToken);

public enum LogoutResult
{
    // O token informado não vale mais: estava ativo e foi revogado agora, ou já estava revogado ou
    // expirado. Um token antigo, já rotacionado, não encerra o token atual da sessão.
    SessionEnded,

    // Token vazio ou inexistente.
    InvalidToken,
}
