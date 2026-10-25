namespace Ouroboros.Identity.Application.Security;

internal sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);
