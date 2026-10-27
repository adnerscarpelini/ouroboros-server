using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.Parameters;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Domain.Parameters;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Parameters;

internal interface IParameterAdministrationReader
{
    Task<bool?> ReadSensitivityAsync(string key, CancellationToken cancellationToken);
    Task<bool> IsActiveAdminAsync(long userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ActiveAdminEmailsAsync(CancellationToken cancellationToken);
}

internal sealed class ChangeParameter(ISessionStore sessions, IPasswordHasher hasher, IParameterPersistence persistence,
    IParameterAdministrationReader reader, IEmailIntentFactory emails, TimeProvider clock) : IChangeParameter
{
    public async Task<ChangeParameterResult> ExecuteAsync(ChangeParameterRequest request, CancellationToken cancellationToken)
    {
        var user = await sessions.FindUserAsync(request.UserId, cancellationToken);
        if (user is null || !user.IsActive || user.Role != UserRole.Admin || user.IsLockedAt(clock.GetUtcNow())
            || hasher.Verify(user.PasswordHash, request.CurrentPassword ?? "") == PasswordVerificationOutcome.Failed)
        { return new(ChangeParameterOutcome.InvalidCredentials); }

        // A reautenticação cara precede o lock. O estado é confirmado novamente sob ele.
        await using var transaction = await sessions.BeginTransactionAsync(cancellationToken);
        await sessions.LockUserAsync(user.Id, cancellationToken);
        var current = await sessions.ReadUserStateAsync(user.Id, cancellationToken);
        if (!current.IsActive || current.LockedUntil > clock.GetUtcNow() || current.PasswordHash != user.PasswordHash
            || !await reader.IsActiveAdminAsync(user.Id, cancellationToken))
        { return new(ChangeParameterOutcome.InvalidCredentials); }

        var sensitive = await reader.ReadSensitivityAsync(request.Key, cancellationToken);
        if (sensitive is null) { return new(ChangeParameterOutcome.NotFound); }
        var now = clock.GetUtcNow();
        var intents = new List<EmailIntent>();
        if (sensitive.Value)
        {
            foreach (var recipient in await reader.ActiveAdminEmailsAsync(cancellationToken))
            {
                var content = new EmailMessage(recipient, "Parâmetro de segurança alterado",
                    $"O parâmetro {request.Key} foi alterado por {request.UserId} em {now:O}.");
                intents.Add(emails.Create(content, "parameter-change", now, now.AddHours(24)));
            }
        }

        try
        {
            await persistence.ChangeWithIntentsAsync(request.Key, request.Value, request.Description,
                request.UserId, request.Revision, now, intents, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(ChangeParameterOutcome.Changed);
        }
        catch (ParameterNotFoundException) { return new(ChangeParameterOutcome.NotFound); }
        catch (ParameterConflictException) { return new(ChangeParameterOutcome.Conflict); }
        catch (InvalidParameterException exception) { return new(ChangeParameterOutcome.InvalidValue, exception.Rule); }
    }
}
