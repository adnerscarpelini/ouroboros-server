namespace Ouroboros.Identity.Contracts.Sessions;

// Caso de uso: entrar com e-mail e senha, abrindo uma sessão nova. Cada host informa o seu canal,
// e o login só aceita o papel daquele canal.
public interface ILogin
{
    Task<LoginResult> ExecuteAsync(LoginRequest request, CancellationToken cancellationToken);
}

public sealed record LoginRequest(string Email, string Password, LoginChannel Channel);

public enum LoginChannel
{
    // API externa: só usuários Customer.
    CustomerApp,

    // Backoffice: só usuários Admin.
    Backoffice,
}

// Falha não diz o motivo (e-mail inexistente, senha errada, usuário inativo ou de outro papel),
// para não revelar se a conta existe.
public sealed record LoginResult(SessionTokens? Tokens)
{
    public static LoginResult InvalidCredentials { get; } = new((SessionTokens?)null);

    public bool Succeeded => Tokens is not null;
}

public sealed record SessionTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);
