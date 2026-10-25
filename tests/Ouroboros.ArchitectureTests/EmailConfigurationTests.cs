namespace Ouroboros.ArchitectureTests;

// O adaptador de e-mail local (pickup directory) só pode ser usado em Development.
public sealed class EmailConfigurationTests
{
    [Fact]
    public void Host_fails_to_start_with_local_email_adapter_outside_development()
    {
        using var factory = new OuroborosApiFactory(
            OuroborosApiFactory.FakeConnectionString,
            new Dictionary<string, string?>
            {
                ["Identity:Email:Provider"] = "PickupDirectory",
                ["Identity:Email:PickupDirectory"] = "../../.local/emails",
            });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("só pode ser usado em Development", exception.Message);
    }

    [Fact]
    public void Host_starts_without_email_provider()
    {
        using var factory = new OuroborosApiFactory();

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }
}
