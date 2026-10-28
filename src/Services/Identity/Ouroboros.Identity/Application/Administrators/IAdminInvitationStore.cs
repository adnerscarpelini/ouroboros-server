using Ouroboros.Identity.Domain.Administrators;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Administrators;

internal interface IAdminInvitationStore
{
    Task LockAdministrationAsync(CancellationToken cancellationToken);
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);
    Task<bool> HasOpenInvitationAsync(string email, DateTimeOffset now, CancellationToken cancellationToken);
    Task<AdminInvitation?> FindTokenAsync(byte[] hash, DateTimeOffset now, CancellationToken cancellationToken);
    Task<AdminInvitation?> FindAsync(Guid id, CancellationToken cancellationToken);
    void Add(AdminInvitation invitation);
    void AddUser(User user);
    void AddIntents(IEnumerable<EmailIntent> intents);
    Task<bool> SaveAsync(CancellationToken cancellationToken);
}
