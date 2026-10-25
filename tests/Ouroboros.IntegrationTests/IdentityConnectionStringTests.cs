using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// A connection string própria do serviço (ConnectionStrings:Identity) tem precedência sobre a do banco original
// (ConnectionStrings:Ouroboros). Remover a primeira é o retorno à connection string anterior, antes de novas escritas.
public sealed class IdentityConnectionStringTests
{
    private const string Legacy = "Host=legacy;Database=ouroboros;Username=admin;Password=x";
    private const string Own = "Host=own;Database=ouroboros_identity;Username=identity_runtime;Password=y";

    [Fact]
    public async Task Own_connection_string_wins_over_the_original_database()
    {
        await using var api = Create(Legacy, own: Own);

        Assert.Equal(Own, api.Services.GetRequiredService<IOptions<IdentityDatabaseOptions>>().Value.ConnectionString);
    }

    [Fact]
    public async Task Without_the_own_connection_string_the_original_database_is_used()
    {
        await using var api = Create(Legacy, own: null);

        Assert.Equal(Legacy, api.Services.GetRequiredService<IOptions<IdentityDatabaseOptions>>().Value.ConnectionString);
    }

    [Fact]
    public async Task Missing_both_connection_strings_prevents_startup_and_names_both_keys()
    {
        await using var api = Create(legacy: "", own: " ");

        var error = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains("ConnectionStrings:Identity", error.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings:Ouroboros", error.Message, StringComparison.Ordinal);
    }

    private static ApiFactory Create(string legacy, string? own) => new IntegrationApiFactory(legacy).WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Identity"] = own })));
}
