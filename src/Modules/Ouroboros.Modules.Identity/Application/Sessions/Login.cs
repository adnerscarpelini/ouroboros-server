using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Contracts.Sessions;
using Ouroboros.Modules.Identity.Domain.Sessions;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;
using Ouroboros.Modules.Identity.Infrastructure.Security;

namespace Ouroboros.Modules.Identity.Application.Sessions;

internal sealed class Login(
    IdentityDbContext context,
    UserPasswordVerifier passwordVerifier,
    AccessTokenIssuer accessTokenIssuer,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider) : ILogin
{
    public async Task<LoginResult> ExecuteAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        // Senha ausente (chamada fora do HTTP) segue o mesmo caminho de uma senha errada.
        var password = request.Password ?? "";
        var user = string.IsNullOrWhiteSpace(request.Email)
            ? null
            : await context.Users.SingleOrDefaultAsync(user => user.Email == User.NormalizeEmail(request.Email), cancellationToken);

        // A senha é sempre verificada, mesmo sem usuário, para o tempo de resposta não indicar se o e-mail existe.
        if (user is null)
        {
            passwordVerifier.VerifyWithoutUser(password);
            return LoginResult.InvalidCredentials;
        }

        // Usuário inativo ou de outro papel recebe a mesma resposta de senha errada.
        if (!passwordVerifier.Verify(user, password) || !user.IsActive || user.Role != RoleOf(request.Channel))
        {
            return LoginResult.InvalidCredentials;
        }

        var (refreshToken, refreshTokenHash) = RefreshTokens.Generate();
        var refreshTokenExpiresAt = timeProvider.GetUtcNow().Add(jwtOptions.Value.RefreshTokenLifetime);
        var session = Session.Open(user.Id, refreshTokenHash, refreshTokenExpiresAt);
        context.Sessions.Add(session);

        // Grava a sessão e, se a verificação refez o hash da senha, o hash novo.
        await context.SaveChangesAsync(cancellationToken);

        var accessToken = accessTokenIssuer.Issue(user, session.ExternalId);
        return new LoginResult(new SessionTokens(accessToken.Token, accessToken.ExpiresAt, refreshToken, refreshTokenExpiresAt));
    }

    private static UserRole RoleOf(LoginChannel channel) => channel switch
    {
        LoginChannel.CustomerApp => UserRole.Customer,
        LoginChannel.Backoffice => UserRole.Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Canal de login desconhecido."),
    };
}
