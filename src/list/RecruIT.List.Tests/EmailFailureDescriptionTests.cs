using System.Net.Sockets;
using MailKit.Net.Smtp;
using MimeKit;
using RecruIT.List.Options;
using RecruIT.List.Services.Notify;

namespace RecruIT.List.Tests;

/// <summary>What the admin test page is told about a failed test email, without knowing MailKit.</summary>
public sealed class EmailFailureDescriptionTests
{
    private static INotificationChannel Channel =>
        new EmailNotificationChannel(
            Microsoft.Extensions.Options.Options.Create(
                new EmailNotificationOptions
                {
                    PublicBaseUrl = "https://recruit.example.org/",
                    From = "recruit@example.org",
                    SubjectTemplate = "{0}",
                    SmtpHost = "smtp.example.org",
                }
            ),
            new FakeStringLocalizer()
        );

    [Fact]
    public void A_rejected_recipient_reports_the_servers_verdict_and_the_mailbox()
    {
        var ex = new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted,
            SmtpStatusCode.MailboxUnavailable,
            new MailboxAddress("", "nobody@example.org"),
            "550 no such user"
        );

        var details = Channel.DescribeFailure(ex);

        Assert.Equal(
            [
                new FailureDetail(
                    "App.NotificationTest.SmtpStatusCodeLabel",
                    "550 (MailboxUnavailable)"
                ),
                new FailureDetail(
                    "App.NotificationTest.SmtpErrorCodeLabel",
                    "RecipientNotAccepted"
                ),
                new FailureDetail("App.NotificationTest.MailboxLabel", "nobody@example.org"),
            ],
            details
        );
    }

    [Fact]
    public void A_refused_connection_is_found_beneath_its_wrappers()
    {
        var ex = new IOException(
            "Could not connect",
            new InvalidOperationException(
                "wrapper",
                new SocketException((int)SocketError.ConnectionRefused)
            )
        );

        var detail = Assert.Single(Channel.DescribeFailure(ex));

        Assert.Equal(
            new FailureDetail("App.NotificationTest.SocketErrorLabel", "ConnectionRefused"),
            detail
        );
    }

    [Fact]
    public void Any_other_failure_has_nothing_to_add() =>
        Assert.Empty(Channel.DescribeFailure(new TimeoutException()));
}
