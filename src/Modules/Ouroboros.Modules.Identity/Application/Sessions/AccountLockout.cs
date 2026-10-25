using System.Globalization;
using Microsoft.Extensions.Logging;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Settings.Contracts;

namespace Ouroboros.Modules.Identity.Application.Sessions;

// Bloqueio da conta após falhas seguidas de senha. O limite e a duração vêm dos parâmetros do sistema,
// lidos no início de todo login.
internal sealed partial class AccountLockout(
    ISessionStore store,
    ISystemParameters parameters,
    ILogger<AccountLockout> logger)
{
    public const string MaxFailedAttemptsParameter = "Identity:Lockout:MaxFailedAttempts";
    public const string DurationMinutesParameter = "Identity:Lockout:DurationMinutes";

    // Lida no início de todo login, com ou sem usuário: parâmetro ausente ou inválido falha igual para
    // qualquer e-mail, e o custo das leituras não indica se a conta existe.
    public async Task<LockoutPolicy> ReadPolicyAsync(CancellationToken cancellationToken) => new(
        await ReadPositiveIntegerAsync(MaxFailedAttemptsParameter, cancellationToken),
        TimeSpan.FromMinutes(await ReadPositiveIntegerAsync(DurationMinutesParameter, cancellationToken)));

    // Conta a falha com um UPDATE atômico, para que falhas simultâneas não se percam. Ao atingir o limite,
    // bloqueia a conta e zera o contador na mesma instrução.
    public async Task RegisterFailureAsync(User user, LockoutPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lockedUntil = now.Add(policy.Duration);
        if (await store.RegisterFailureAsync(user.Id, policy.MaxFailedAttempts, lockedUntil, now, cancellationToken))
        {
            LogLocked(user.ExternalId, lockedUntil);
        }
    }

    private async Task<int> ReadPositiveIntegerAsync(string key, CancellationToken cancellationToken)
    {
        var value = await parameters.GetAsync(key, cancellationToken);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new InvalidOperationException($"O parâmetro do sistema '{key}' deve ser um inteiro positivo.");
    }

    // Só o id externo da conta: nunca a senha nem o e-mail.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Conta {UserId} bloqueada até {LockedUntil} após falhas seguidas de senha.")]
    private partial void LogLocked(Guid userId, DateTimeOffset lockedUntil);
}

internal sealed record LockoutPolicy(int MaxFailedAttempts, TimeSpan Duration);
