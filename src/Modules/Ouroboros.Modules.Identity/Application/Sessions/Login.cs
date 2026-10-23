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
    AccountLockout accountLockout,
    AccessTokenIssuer accessTokenIssuer,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider) : ILogin
{
    public async Task<LoginResult> ExecuteAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        // Senha ausente (chamada fora do HTTP) segue o mesmo caminho de uma senha errada.
        var password = request.Password ?? "";
        var lockoutPolicy = await accountLockout.ReadPolicyAsync(cancellationToken);
        var user = string.IsNullOrWhiteSpace(request.Email)
            ? null
            : await context.Users.SingleOrDefaultAsync(user => user.Email == User.NormalizeEmail(request.Email), cancellationToken);

        // Sem usuário ou com a conta bloqueada, a senha real não é verificada, mas uma verificação contra
        // um hash fictício roda mesmo assim, para o tempo de resposta não indicar se o e-mail existe.
        var now = timeProvider.GetUtcNow();
        if (user is null || user.IsLockedAt(now))
        {
            passwordVerifier.VerifyWithoutUser(password);
            return LoginResult.InvalidCredentials;
        }

        if (!passwordVerifier.Verify(user, password))
        {
            await accountLockout.RegisterFailureAsync(user, lockoutPolicy, now, cancellationToken);
            return LoginResult.InvalidCredentials;
        }

        // Usuário inativo ou de outro papel recebe a mesma resposta de senha errada.
        if (!user.IsActive || user.Role != LoginChannels.RoleOf(request.Channel))
        {
            return LoginResult.InvalidCredentials;
        }

        user.ResetFailedLogins();
        var (refreshToken, refreshTokenHash) = OpaqueTokens.Generate();
        var refreshTokenExpiresAt = now.Add(jwtOptions.Value.RefreshTokenLifetime);
        var session = Session.Open(user.Id, refreshTokenHash, refreshTokenExpiresAt);
        context.Sessions.Add(session);

        // Grava a sessão, o contador zerado e, se a verificação refez o hash da senha, o hash novo.
        await context.SaveChangesAsync(cancellationToken);

        var accessToken = accessTokenIssuer.Issue(user, session.ExternalId);
        return new LoginResult(new SessionTokens(accessToken.Token, accessToken.ExpiresAt, refreshToken, refreshTokenExpiresAt));
    }
}
