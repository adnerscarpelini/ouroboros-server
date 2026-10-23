using System.ComponentModel.DataAnnotations;
using Ouroboros.Modules.Identity.Contracts.Sessions;

namespace Ouroboros.Api.Controllers.Identity;

// Os limites de tamanho só protegem o servidor de entradas enormes; o formato do e-mail não é
// validado aqui, e um e-mail inválido recebe o mesmo 401 de credenciais erradas.
public sealed record CreateSessionRequest(
    [Required, MaxLength(254)] string Email,
    [Required, MaxLength(1024)] string Password);

// Corpo da renovação e do logout. Token vazio ou ausente não é erro de validação: recebe o mesmo 401
// de token inexistente.
public sealed record RefreshSessionBody([MaxLength(256)] string? RefreshToken);

// Resposta do login e da renovação (RFC 6749, seção 5.1, com as expirações como instantes).
public sealed record SessionResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    public static SessionResponse From(SessionTokens tokens) =>
        new(tokens.AccessToken, "Bearer", tokens.AccessTokenExpiresAt, tokens.RefreshToken, tokens.RefreshTokenExpiresAt);
}

// Tipos de erro do catálogo do Identity usados por este host.
internal static class IdentityErrors
{
    public const string InvalidCredentials =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-credentials";

    public const string InvalidCredentialsTitle = "E-mail ou senha inválidos.";

    public const string InvalidRefreshToken =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-refresh-token";

    public const string InvalidRefreshTokenTitle = "Refresh token inválido.";

    public const string InvalidRegistrationToken =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-registration-token";

    public const string InvalidRegistrationTokenTitle = "Link de cadastro inválido ou expirado.";

    public const string InvalidPasswordResetToken =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-password-reset-token";

    public const string InvalidPasswordResetTokenTitle = "Link de redefinição de senha inválido ou expirado.";

    public const string PasswordRejected =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#password-rejected";

    public const string PasswordRejectedTitle = "A senha não atende à política de senha.";
}
