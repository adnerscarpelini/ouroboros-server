using Ouroboros.Identity.Domain.Notifications;

namespace Ouroboros.Identity.Application.Notifications;

internal interface IEmailIntentFactory
{
    EmailIntent Create(EmailMessage content, string purpose, DateTimeOffset now, DateTimeOffset expiresAt);
}
