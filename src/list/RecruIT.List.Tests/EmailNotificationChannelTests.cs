using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit.Security;
using RecruIT.List.Options;
using RecruIT.List.Services.Notify;

namespace RecruIT.List.Tests;

/// <summary>Sending through a real SMTP server - the same MailDev as src/hack/compose.yaml.</summary>
public sealed class EmailNotificationChannelTests(MailDevFixture mailDev)
    : IClassFixture<MailDevFixture>
{
    private EmailNotificationChannel CreateChannel(string password = MailDevFixture.Password) =>
        new(
            Microsoft.Extensions.Options.Options.Create(
                new EmailNotificationOptions
                {
                    PublicBaseUrl = "https://recruit.example.org/",
                    From = "recruit@example.org",
                    SubjectTemplate = "{0}",
                    SmtpHost = mailDev.Host,
                    SmtpPort = mailDev.SmtpPort,
                    SmtpUsername = MailDevFixture.Username,
                    SmtpPassword = password,
                }
            )
        );

    [Fact]
    public async Task A_batch_sends_each_message_as_html_from_the_configured_sender()
    {
        var subject = $"New recommendations {Guid.NewGuid():N}";
        var ct = TestContext.Current.CancellationToken;

        await using (var batch = await CreateChannel().BeginBatchAsync(ct: ct))
        {
            await batch.SendAsync("alice@example.org", subject, "<p>Two new patients</p>", ct);
            await batch.SendAsync("bob@example.org", subject, "<p>Two new patients</p>", ct);
        }

        var received = (await mailDev.ReceivedAsync(ct)).Where(m => m.Subject == subject).ToList();
        Assert.Equal(
            ["alice@example.org", "bob@example.org"],
            received.Select(m => Assert.Single(m.To).Address).Order()
        );
        Assert.All(
            received,
            m =>
            {
                Assert.Equal("recruit@example.org", Assert.Single(m.From).Address);
                Assert.Contains("<p>Two new patients</p>", m.Html);
            }
        );
    }

    [Fact]
    public async Task The_protocol_log_shows_the_exchange_but_not_the_password()
    {
        using var log = new MemoryStream();
        var ct = TestContext.Current.CancellationToken;

        await using (var batch = await CreateChannel().BeginBatchAsync(log, ct))
        {
            await batch.SendAsync("alice@example.org", "Logged", "<p>Hi</p>", ct);
        }

        var transcript = Encoding.UTF8.GetString(log.ToArray());
        Assert.Contains("AUTH", transcript);
        Assert.DoesNotContain(MailDevFixture.Password, transcript);
        // Neither as AUTH LOGIN sends it, nor as AUTH PLAIN's "\0user\0password".
        Assert.DoesNotContain(Base64(MailDevFixture.Password), transcript);
        Assert.DoesNotContain(
            Base64($"\0{MailDevFixture.Username}\0{MailDevFixture.Password}"),
            transcript
        );
    }

    private static string Base64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    [Fact]
    public async Task Wrong_credentials_fail_the_batch_before_anything_is_sent() =>
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            CreateChannel(password: "wrong")
                .BeginBatchAsync(ct: TestContext.Current.CancellationToken)
        );
}

public sealed class MailDevFixture : IAsyncLifetime
{
    public const string Username = "recruit";
    public const string Password = "smtp-secret-123";

    private const int Smtp = 1025;
    private const int Web = 1080;

    private readonly IContainer _mailDev = new ContainerBuilder(
        "docker.io/maildev/maildev:3.0.0@sha256:6e09695961f4aef9016a390ad391314a078220216601b15a35d389a7df2a3855"
    )
        .WithPortBinding(Smtp, assignRandomHostPort: true)
        .WithPortBinding(Web, assignRandomHostPort: true)
        .WithEnvironment("MAILDEV_INCOMING_USER", Username)
        .WithEnvironment("MAILDEV_INCOMING_PASS", Password)
        .WithWaitStrategy(
            Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request =>
                    request.ForPort(Web).ForPath("/api/healthz")
                )
        )
        .Build();

    public string Host => _mailDev.Hostname;

    public int SmtpPort => _mailDev.GetMappedPublicPort(Smtp);

    public async ValueTask InitializeAsync() => await _mailDev.StartAsync();

    public async ValueTask DisposeAsync() => await _mailDev.DisposeAsync();

    public async Task<IReadOnlyList<ReceivedEmail>> ReceivedAsync(CancellationToken ct)
    {
        using var http = new HttpClient();
        var url =
            $"http://{Host}:{_mailDev.GetMappedPublicPort(Web).ToString(CultureInfo.InvariantCulture)}/api/email";
        return await http.GetFromJsonAsync<List<ReceivedEmail>>(url, ct) ?? [];
    }

    public sealed record ReceivedEmail(
        string Subject,
        IReadOnlyList<Mailbox> From,
        IReadOnlyList<Mailbox> To,
        string Html
    );

    public sealed record Mailbox(string Address);
}
