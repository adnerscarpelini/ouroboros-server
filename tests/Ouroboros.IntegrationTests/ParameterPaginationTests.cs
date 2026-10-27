using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Contracts.Parameters;
using Ouroboros.Identity.Domain.Parameters;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class ParameterPaginationTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Route = "/api/v1/admin/settings/parameters";
    private const string Password = "uma frase longa de teste para acesso";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Default_page_has_fifty_rows_and_keyset_continuation_does_not_repeat_after_insertion_before_cursor()
    {
        await SeedAsync(60);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminClientAsync(api);
        var first = await PageAsync(client, Route);
        Assert.Equal(50, first.Data.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);
        var originalCount = await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameters");

        await SeedParametersAsync(1, "inserted");
        var earlier = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-1));
        await ExecuteAsync($"UPDATE identity.parameters SET external_id = '{earlier}' WHERE key = 'inserted:0'");
        var second = await PageAsync(client, Route + "?after=" + first.NextCursor);
        Assert.False(second.HasMore);
        Assert.Null(second.NextCursor);
        var all = first.Data.Concat(second.Data).ToArray();
        Assert.Equal(originalCount, all.Length);
        Assert.Equal(all.Length, all.Select(row => row.Id).Distinct().Count());
        Assert.DoesNotContain(all, row => row.Key == "inserted:0");
        Assert.Equal(all.Select(row => row.Id).Order(), all.Select(row => row.Id));
        Assert.All(all, row => Assert.False(string.IsNullOrWhiteSpace(row.DataType)));
    }

    [Theory]
    [InlineData("?limit=101")]
    [InlineData("?limit=0")]
    [InlineData("?limit=-1")]
    [InlineData("?limit=abc")]
    [InlineData("?after=invalid")]
    [InlineData("?after=")]
    public async Task Invalid_page_returns_400(string query)
    {
        await SeedAsync(0);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminClientAsync(api);
        using var response = await client.GetAsync(Route + query, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Cursor_for_another_query_is_rejected_and_limit_may_change_between_pages()
    {
        await SeedAsync(0);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminClientAsync(api);
        var first = await PageAsync(client, Route + "?limit=1");
        var second = await PageAsync(client, Route + "?limit=2&after=" + first.NextCursor);
        Assert.Equal(2, second.Data.Count);
        Assert.DoesNotContain(second.Data, row => row.Id == first.Data[0].Id);
        var bytes = Convert.FromBase64String(first.NextCursor!.Replace('-', '+').Replace('_', '/').PadRight((first.NextCursor.Length + 3) / 4 * 4, '='));
        var text = System.Text.Encoding.UTF8.GetString(bytes).Replace("identity:parameters:v1:external-id:asc", "other-query");
        var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Route + "?after=" + cursor, Cancellation)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parameters_require_admin_audience_and_current_active_admin_role(bool deactivate)
    {
        await SeedAsync(0);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route, Cancellation)).StatusCode);
        // A política padrão desafia chamadas anônimas inclusive em rotas ausentes no listener público.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(Route, Cancellation)).StatusCode);
        using var admin = await AdminClientAsync(api);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Route, Cancellation)).StatusCode);
        await ExecuteAsync(deactivate
            ? "UPDATE identity.users SET is_active = false WHERE email = 'admin@adnerscarpelini.com'"
            : "UPDATE identity.users SET role = 'Customer' WHERE email = 'admin@adnerscarpelini.com'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync(Route, Cancellation)).StatusCode);
    }

    [Fact]
    public async Task Maximum_page_limit_returns_exact_envelope_and_last_page_has_no_cursor()
    {
        await SeedAsync(105);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminClientAsync(api);
        using var response = await client.GetAsync(Route + "?limit=100", Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.Equal(new[] { "data", "hasMore", "nextCursor" }, json.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(100, json.GetProperty("data").GetArrayLength());
        var row = json.GetProperty("data")[0];
        Assert.Equal(new[] { "dataType", "description", "id", "isSensitive", "key", "revision", "updatedAt", "updatedBy", "validationPattern", "value" },
            row.EnumerateObject().Select(property => property.Name).Order());
        var second = await PageAsync(client, Route + "?limit=100&after=" + json.GetProperty("nextCursor").GetString());
        Assert.False(second.HasMore);
        Assert.Null(second.NextCursor);
        Assert.Equal(await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameters") - 100, second.Data.Count);
    }

    [Theory]
    [InlineData(2, "01900000-0000-7000-8000-000000000001")]
    [InlineData(1, "00000000-0000-0000-0000-000000000000")]
    public async Task Unsupported_version_or_empty_position_cursor_is_rejected(int version, string position)
    {
        await SeedAsync(0);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminClientAsync(api);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(new { Version = version, Scope = "identity:parameters:v1:external-id:asc", Position = position });
        var cursor = Convert.ToBase64String(encoded).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Route + "?after=" + cursor, Cancellation)).StatusCode);
    }

    private async Task SeedAsync(int count)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        db.Users.Add(User.Create("admin@adnerscarpelini.com", scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), UserRole.Admin));
        await db.SaveChangesAsync(Cancellation);
        await SeedParametersAsync(count, "paging");
    }

    private async Task SeedParametersAsync(int count, string prefix)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        for (var i = 0; i < count; i++)
        {
            var (parameter, history) = Parameter.Create($"{prefix}:{i}", "value", "Descrição", ParameterDataType.String, null, false, null, DateTimeOffset.UtcNow);
            db.Parameters.Add(parameter);
            db.ParameterHistory.Add(history);
        }
        await db.SaveChangesAsync(Cancellation);
    }

    private static async Task<HttpClient> AdminClientAsync(BackofficeIntegrationApiFactory api)
    {
        var client = api.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/admin/identity/sessions", new { Email = "admin@adnerscarpelini.com", Password }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<CursorPage<ParameterView>> PageAsync(HttpClient client, string uri)
    {
        using var response = await client.GetAsync(uri, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CursorPage<ParameterView>>(Cancellation))!;
    }
}
