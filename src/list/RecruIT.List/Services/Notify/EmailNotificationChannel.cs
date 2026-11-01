using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using MimeKit;
using RecruIT.List.Options;
using RecruIT.List.Resources;

namespace RecruIT.List.Services.Notify;

public sealed class EmailNotificationChannel(
    IOptions<EmailNotificationOptions> mailerOptions,
    IStringLocalizer<SharedResources> localizer
) : INotificationChannel
{
    public async Task<INotificationBatch> BeginBatchAsync(
        Stream? protocolLog = null,
        CancellationToken ct = default
    )
    {
        var options = mailerOptions.Value;

        // RedactSecrets masks the AUTH exchange's credentials, so the transcript is safe to show to
        // an admin - set explicitly rather than relying on MailKit's undocumented default.
        var client = new SmtpClient(
            protocolLog is null
                ? new NullProtocolLogger()
                : new ProtocolLogger(protocolLog, leaveOpen: true) { RedactSecrets = true }
        );
        try
        {
            await client.ConnectAsync(
                options.SmtpHost,
                options.SmtpPort,
                SecureSocketOptions.Auto,
                ct
            );
            if (
                !string.IsNullOrEmpty(options.SmtpUsername)
                && client.AuthenticationMechanisms.Count > 0
            )
            {
                await client.AuthenticateAsync(
                    options.SmtpUsername,
                    options.SmtpPassword ?? "",
                    ct
                );
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new EmailBatch(client, options.From);
    }

    public IReadOnlyList<FailureDetail> DescribeFailure(Exception ex)
    {
        var details = new List<FailureDetail>();

        if (ex is SmtpCommandException command)
        {
            // The server's own verdict - e.g. 535 for rejected credentials, 550 for a refused mailbox.
            details.Add(
                new(
                    localizer["App.NotificationTest.SmtpStatusCodeLabel"],
                    $"{(int)command.StatusCode} ({command.StatusCode})"
                )
            );
            details.Add(
                new(
                    localizer["App.NotificationTest.SmtpErrorCodeLabel"],
                    command.ErrorCode.ToString()
                )
            );
            if (command.Mailbox is not null)
            {
                details.Add(
                    new(localizer["App.NotificationTest.MailboxLabel"], command.Mailbox.Address)
                );
            }
        }

        // Connection failures (refused, DNS, timeout) usually arrive wrapped, so look past the
        // outermost exception for the socket-level cause.
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException socket)
            {
                details.Add(
                    new(
                        localizer["App.NotificationTest.SocketErrorLabel"],
                        socket.SocketErrorCode.ToString()
                    )
                );
                break;
            }
        }

        return details;
    }

    private sealed class EmailBatch(SmtpClient client, string from) : INotificationBatch
    {
        public async Task SendAsync(
            string recipient,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        )
        {
            using var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(from));
            message.To.Add(MailboxAddress.Parse(recipient));
            message.Subject = subject;
            message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

            await client.SendAsync(message, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true);
            }
            client.Dispose();
        }
    }
}
