namespace Ouroboros.ArchitectureTests;

public sealed class DatabaseConfigurationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Host_fails_to_start_without_connection_string(string connectionString)
    {
        using var factory = new OuroborosApiFactory(connectionString);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:Ouroboros", exception.Message);
    }

    [Fact]
    public void Host_starts_with_connection_string()
    {
        using var factory = new OuroborosApiFactory();

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }
}
