using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Contracts.Sessions;
using Ouroboros.Modules.Identity.Domain.Sessions;
using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Application.Sessions;

internal sealed class Login(
    ISessionStore store,
    UserPasswordVerifier passwordVerifier,
    AccountLockout accountLockout,
    IAccessTokenIssuer accessTokenIssuer,
    SessionTokenOptions tokenOptions,
    TimeProvider timeProvider) : ILogin
{
    public async Task<LoginResult> ExecuteAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        // Senha ausente (chamada fora do HTTP) segue o mesmo caminho de uma senha errada.
        var password = request.Password ?? "";
        var lockoutPolicy = await accountLockout.ReadPolicyAsync(cancellationToken);
        var user = string.IsNullOrWhiteSpace(request.Email)
            ? null
            : await store.FindUserAsync(User.NormalizeEmail(request.Email), cancellationToken);

        // Sem usuário ou com a conta bloqueada, a senha real não é verificada, mas uma verificação contra
        // um hash fictício roda mesmo assim, para o tempo de resposta não indicar se o e-mail existe.
        var now = timeProvider.GetUtcNow();
        if (user is null || user.IsLockedAt(now))
        {
            passwordVerifier.VerifyWithoutUser(password);
            return LoginResult.InvalidCredentials;
        }

        // Hash conferido, guardado antes de um eventual rehash, para checar depois que a senha não mudou.
        var verifiedHash = user.PasswordHash;
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

        // A sessão é aberta com o usuário travado. Se a senha mudou (por exemplo, numa redefinição), a conta
        // foi desativada ou bloqueada entre a verificação e agora, o login é recusado: sem isso, um login com
        // a senha antiga abriria uma sessão depois de a redefinição revogar as outras, ou o rehash gravaria o
        // hash antigo por cima do novo.
        await using var transaction = await store.BeginTransactionAsync(cancellationToken);
        await store.LockUserAsync(user.Id, cancellationToken);
        var current = await store.ReadUserStateAsync(user.Id, cancellationToken);
        if (current.PasswordHash != verifiedHash || !current.IsActive || current.LockedUntil > now)
        {
            return LoginResult.InvalidCredentials;
        }

        user.ResetFailedLogins();
        var (refreshToken, refreshTokenHash) = OpaqueTokens.Generate();
        var refreshTokenExpiresAt = now.Add(tokenOptions.RefreshTokenLifetime);
        var session = Session.Open(user.Id, refreshTokenHash, refreshTokenExpiresAt);
        store.AddSession(session);

        // Grava a sessão, o contador zerado e, se a verificação refez o hash da senha, o hash novo.
        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var accessToken = accessTokenIssuer.Issue(user.ExternalId, user.Role, session.ExternalId);
        return new LoginResult(new SessionTokens(accessToken.Token, accessToken.ExpiresAt, refreshToken, refreshTokenExpiresAt));
    }
}
