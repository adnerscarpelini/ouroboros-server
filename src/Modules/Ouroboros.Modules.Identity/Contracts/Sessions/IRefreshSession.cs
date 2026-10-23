namespace Ouroboros.Modules.Identity.Contracts.Sessions;

// Caso de uso: trocar um refresh token ativo por um par novo, na mesma sessão. O token recebido é
// revogado (uso único), e o host informa o seu canal, como no login.
public interface IRefreshSession
{
    Task<RefreshSessionResult> ExecuteAsync(RefreshSessionRequest request, CancellationToken cancellationToken);
}

public sealed record RefreshSessionRequest(string? RefreshToken, LoginChannel Channel);

// Falha não diz o motivo (token inexistente, revogado, expirado, usuário inativo ou de outro papel).
public sealed record RefreshSessionResult(SessionTokens? Tokens)
{
    public static RefreshSessionResult InvalidToken { get; } = new((SessionTokens?)null);

    public bool Succeeded => Tokens is not null;
}
