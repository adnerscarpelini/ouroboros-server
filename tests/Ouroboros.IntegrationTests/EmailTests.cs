using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Infrastructure.Email;

namespace Ouroboros.IntegrationTests;

// Testes de unidade da porta de e-mail; não usam banco.
public sealed class EmailTests : IDisposable
{
    private const string Token = "token-secreto-de-teste-0123456789";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ouroboros-emails-{Guid.NewGuid():N}");

    [Fact]
    public async Task Pickup_directory_writes_eml_with_recipient_subject_and_body()
    {
        var logs = new ListLogger<PickupDirectoryEmailSender>();
        var sender = CreateSender(logs);

        await sender.SendAsync(
            new EmailMessage("cliente@example.com", "Confirme seu cadastro", $"Acesse https://ouroboros.local/confirmar?token={Token}"),
            TestContext.Current.CancellationToken);

        var file = Assert.Single(Directory.GetFiles(_directory, "*.eml"));
        var content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        Assert.Contains("To: cliente@example.com\r\n", content);
        Assert.Contains("From: nao-responda@ouroboros.local\r\n", content);
        Assert.Contains("Subject: Confirme seu cadastro\r\n", content);
        Assert.Contains("Content-Type: text/plain; charset=utf-8\r\n", content);
        Assert.Contains(Token, content);
    }

    [Fact]
    public async Task Log_contains_neither_body_nor_token()
    {
        var logs = new ListLogger<PickupDirectoryEmailSender>();

        await CreateSender(logs).SendAsync(
            new EmailMessage("cliente@example.com", "Assunto", $"Corpo com token {Token}"),
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(logs.Messages);
        Assert.DoesNotContain(Token, entry);
        Assert.DoesNotContain("Corpo", entry);
    }

    [Fact]
    public void Subject_with_accents_is_encoded()
    {
        var content = PickupDirectoryEmailSender.Format(
            "nao-responda@ouroboros.local",
            new EmailMessage("a@example.com", "Redefinição de senha", "corpo"),
            DateTimeOffset.UnixEpoch);

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("Redefinição de senha"));
        Assert.Contains($"Subject: =?utf-8?B?{encoded}?=\r\n", content);
    }

    [Theory]
    [InlineData("a@example.com\r\nBcc: intruso@example.com", "Assunto")]
    [InlineData("a@example.com", "Assunto\r\nX-Injetado: sim")]
    [InlineData("a@example.com", "Assunto\nX-Injetado: sim")]
    public void Message_rejects_line_breaks_in_headers(string to, string subject) =>
        Assert.Throws<ArgumentException>(() => new EmailMessage(to, subject, "corpo"));

    [Fact]
    public void Date_header_is_written_in_utc()
    {
        var content = PickupDirectoryEmailSender.Format(
            "nao-responda@ouroboros.local",
            new EmailMessage("a@example.com", "Assunto", "corpo"),
            new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(-3)));

        Assert.Contains("Date: Thu, 01 Jan 2026 12:00:00 +0000\r\n", content);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Local_adapter_is_rejected_outside_development(string environmentName)
    {
        var result = new EmailOptionsValidator(new FakeEnvironment(environmentName, _directory))
            .Validate(null, new EmailOptions { Provider = EmailProvider.PickupDirectory, PickupDirectory = _directory });

        Assert.True(result.Failed);
        Assert.Contains("só pode ser usado em Development", result.FailureMessage);
    }

    [Fact]
    public void Local_adapter_is_accepted_in_development()
    {
        var result = new EmailOptionsValidator(new FakeEnvironment(Environments.Development, _directory))
            .Validate(null, new EmailOptions { Provider = EmailProvider.PickupDirectory, PickupDirectory = _directory });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Sending_without_provider_fails_explicitly()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new UnconfiguredEmailSender()
            .SendAsync(new EmailMessage("a@example.com", "s", "b"), TestContext.Current.CancellationToken));

        Assert.Contains("Identity:Email:Provider", exception.Message);
    }

    [Fact]
    public async Task Api_without_provider_resolves_the_unconfigured_sender()
    {
        await using var api = new IntegrationApiFactory("Host=localhost;Database=unused;Username=u;Password=p");

        Assert.IsType<UnconfiguredEmailSender>(api.Services.GetRequiredService<IEmailSender>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private PickupDirectoryEmailSender CreateSender(ILogger<PickupDirectoryEmailSender> logger) => new(
        Options.Create(new EmailOptions { Provider = EmailProvider.PickupDirectory, PickupDirectory = _directory }),
        new FakeEnvironment(Environments.Development, _directory),
        new FakeTimeProvider(),
        logger);

    private sealed class FakeEnvironment(string environmentName, string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Ouroboros.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
