using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Identity.Application.Parameters;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Contracts.Parameters;
using Ouroboros.Identity.Domain.Parameters;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class ParameterHistoryTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Root = "/api/v1/admin/settings/parameters";
    private const string Key = "history:key";
    private const string Password = "uma frase longa para acesso ao historico";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task History_preserves_values_descriptions_author_and_revision_in_descending_keyset_pages()
    {
        var author = await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        var first = await PageAsync(client, Key, "?limit=2");
        Assert.Equal(new[] { 3, 2 }, first.Data.Select(row => row.Revision));
        Assert.True(first.HasMore);
        Assert.Equal("value2", first.Data[0].PreviousValue);
        Assert.Equal("value3", first.Data[0].NewValue);
        Assert.Equal("description2", first.Data[0].PreviousDescription);
        Assert.Equal("description3", first.Data[0].NewDescription);
        Assert.Equal(author, first.Data[0].ChangedBy);
        Assert.Equal(Key, first.Data[0].Key);
        var second = await PageAsync(client, Key, "?limit=2&after=" + first.NextCursor);
        Assert.Equal(new[] { 1, 0 }, second.Data.Select(row => row.Revision));
        Assert.False(second.HasMore);
        Assert.Null(second.NextCursor);
        Assert.Equal(4, first.Data.Concat(second.Data).Select(row => row.Id).Distinct().Count());
    }

    [Fact]
    public async Task Equal_timestamps_use_external_id_tiebreaker_and_cursor_cannot_cross_parameter_or_list()
    {
        await PrepareAsync();
        await ExecuteAsync("UPDATE identity.parameter_history SET changed_at = '2026-01-01T00:00:00Z' WHERE parameter_key = 'history:key'");
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        var all = new List<ParameterHistoryView>();
        string? cursor = null;
        do
        {
            var page = await PageAsync(client, Key, "?limit=1" + (cursor is null ? "" : "&after=" + cursor));
            all.AddRange(page.Data);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(4, all.Count);
        Assert.Equal(all.Select(row => row.Id).OrderDescending(), all.Select(row => row.Id));
        Assert.Equal(4, all.Select(row => row.Id).Distinct().Count());
        var first = await PageAsync(client, Key, "?limit=1");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Root + "/Identity:Lockout:MaxFailedAttempts/history?after=" + first.NextCursor, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Root + "?after=" + first.NextCursor, Cancellation)).StatusCode);
        var listing = await client.GetFromJsonAsync<CursorPage<ParameterView>>(Root + "?limit=1", Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Root + "/" + Key + "/history?after=" + listing!.NextCursor, Cancellation)).StatusCode);
    }

    [Theory]
    [InlineData("?limit=101", HttpStatusCode.BadRequest)]
    [InlineData("?limit=0", HttpStatusCode.BadRequest)]
    [InlineData("?after=", HttpStatusCode.BadRequest)]
    [InlineData("?after=invalid", HttpStatusCode.BadRequest)]
    public async Task Invalid_history_page_returns_400(string query, HttpStatusCode expected)
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        Assert.Equal(expected, (await client.GetAsync(Root + "/" + Key + "/history" + query, Cancellation)).StatusCode);
    }

    [Fact]
    public async Task Missing_parameter_is_404_and_anonymous_or_inactive_admin_cannot_read_history()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Root + "/" + Key + "/history", Cancellation)).StatusCode);
        using var client = await AdminAsync(api);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Root + "/missing/history", Cancellation)).StatusCode);
        await ExecuteAsync("UPDATE identity.users SET is_active = false WHERE email = 'admin@adnerscarpelini.com'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Root + "/" + Key + "/history", Cancellation)).StatusCode);
    }

    [Fact]
    public async Task History_cursor_for_maximum_length_unicode_key_can_be_used_for_next_page()
    {
        var key = new string('ç', 200);
        await PrepareAsync(key);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        var first = await PageAsync(client, Uri.EscapeDataString(key), "?limit=1");
        Assert.True(first.HasMore);
        var second = await PageAsync(client, Uri.EscapeDataString(key), "?limit=1&after=" + first.NextCursor);
        Assert.DoesNotContain(second.Data, row => row.Id == first.Data[0].Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2026-01-01T00:00:00-03:00")]
    public async Task History_cursor_requires_utc_timestamp(string? changedAt)
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        var first = await PageAsync(client, Key, "?limit=1");
        var cursor = first.NextCursor!;
        var bytes = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/').PadRight((cursor.Length + 3) / 4 * 4, '='));
        var payload = JsonNode.Parse(bytes)!;
        payload["ChangedAt"] = changedAt;
        var altered = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var response = await client.GetAsync(Root + "/" + Key + "/history?after=" + altered, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task New_history_inserted_before_cursor_does_not_repeat_or_skip_original_rows()
    {
        var author = await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        var first = await PageAsync(client, Key, "?limit=2");
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ParameterWriter>().ChangeAsync(Key, "value4", "description4", author, Cancellation);
        }

        var second = await PageAsync(client, Key, "?limit=2&after=" + first.NextCursor);
        Assert.Equal(new[] { 3, 2, 1, 0 }, first.Data.Concat(second.Data).Select(row => row.Revision));
        Assert.False(second.HasMore);
        Assert.Equal(4, (await PageAsync(client, Key, "?limit=1")).Data[0].Revision);
    }

    [Fact]
    public async Task Changed_database_role_cannot_read_history_with_old_admin_token()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        await ExecuteAsync("UPDATE identity.users SET role = 'Customer' WHERE email = 'admin@adnerscarpelini.com'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Root + "/" + Key + "/history", Cancellation)).StatusCode);
    }

    private async Task<Guid> PrepareAsync(string key = Key)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = User.Create("admin@adnerscarpelini.com", scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), UserRole.Admin);
        db.Users.Add(user);
        await db.SaveChangesAsync(Cancellation);
        var writer = scope.ServiceProvider.GetRequiredService<ParameterWriter>();
        await writer.CreateAsync(key, "initial", "initial description", ParameterDataType.String, null, true, user.ExternalId, Cancellation);
        for (var revision = 1; revision <= 3; revision++)
        { await writer.ChangeAsync(key, "value" + revision, "description" + revision, user.ExternalId, Cancellation); }
        return user.ExternalId;
    }

    private static async Task<HttpClient> AdminAsync(BackofficeIntegrationApiFactory api)
    {
        var client = api.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/admin/identity/sessions", new { Email = "admin@adnerscarpelini.com", Password }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<CursorPage<ParameterHistoryView>> PageAsync(HttpClient client, string key, string query)
    {
        using var response = await client.GetAsync(Root + "/" + key + "/history" + query, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CursorPage<ParameterHistoryView>>(Cancellation))!;
    }
}
