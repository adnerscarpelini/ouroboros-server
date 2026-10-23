namespace Ouroboros.ArchitectureTests;

// Prova que o host descobre um controller de uma application part registrada e o ativa com as
// dependências da DI.
public sealed class ControllerDiscoveryTests(OuroborosApiFactory factory)
    : IClassFixture<OuroborosApiFactory>
{
    [Fact]
    public async Task Controller_in_application_part_responds_on_its_route()
    {
        using var client = ProbeHost.CreateClient(factory, new ProbeCallCounter());

        var response = await client.GetAsync("/api/v1/probe", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal(ProbeGreeting.Text, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
