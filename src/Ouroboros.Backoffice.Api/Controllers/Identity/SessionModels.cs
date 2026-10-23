using System.ComponentModel.DataAnnotations;
using Ouroboros.Modules.Identity.Contracts.Sessions;

namespace Ouroboros.Backoffice.Api.Controllers.Identity;

// Os limites de tamanho só protegem o servidor de entradas enormes; o formato do e-mail não é
// validado aqui, e um e-mail inválido recebe o mesmo 401 de credenciais erradas.
public sealed record CreateSessionRequest(
    [Required, MaxLength(254)] string Email,
    [Required, MaxLength(1024)] string Password);

// Resposta do login (RFC 6749, seção 5.1, com as expirações como instantes).
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
}
