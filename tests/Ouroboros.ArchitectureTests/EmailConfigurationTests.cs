namespace Ouroboros.ArchitectureTests;

// Identity não hospeda mais adaptador de entrega de e-mail.
public sealed class EmailConfigurationTests
{
    [Fact]
    public void Host_rejects_legacy_email_adapter_configuration()
    {
        using var factory = new OuroborosApiFactory(
            OuroborosApiFactory.FakeConnectionString,
            new Dictionary<string, string?>
            {
                ["Identity:Email:Provider"] = "PickupDirectory",
                ["Identity:Email:PickupDirectory"] = "../../.local/emails",
            });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("o transporte pertence a Notifications", exception.Message);
    }

    [Fact]
    public void Host_starts_without_email_provider()
    {
        using var factory = new OuroborosApiFactory();

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }
}
