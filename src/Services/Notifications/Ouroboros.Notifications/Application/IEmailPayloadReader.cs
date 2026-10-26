using Ouroboros.Messaging.Contracts;
using Ouroboros.Notifications.Contracts;

namespace Ouroboros.Notifications.Application;

internal interface IEmailPayloadReader
{
    EmailContent Read(MessageEnvelope message);
}
