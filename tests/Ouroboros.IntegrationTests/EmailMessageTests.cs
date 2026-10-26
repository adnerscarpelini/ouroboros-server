using Ouroboros.Identity.Application.Notifications;

namespace Ouroboros.IntegrationTests;

public sealed class EmailMessageTests
{
    [Theory]
    [InlineData("a@example.com\r\nBcc: b@example.com", "subject")]
    [InlineData("a@example.com", "subject\nBcc: b@example.com")]
    public void Header_injection_is_rejected_before_preparing_intent(string to, string subject) =>
        Assert.Throws<ArgumentException>(() => new EmailMessage(to, subject, "body"));
}
