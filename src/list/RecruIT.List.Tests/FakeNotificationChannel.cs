using RecruIT.List.Services.Notify;

namespace RecruIT.List.Tests;

/// <summary>An INotificationChannel that sends nothing, remembers who it was asked to send to, and fails on demand.</summary>
internal sealed class FakeNotificationChannel : INotificationChannel
{
    private int _sends;

    public List<string> SentTo { get; } = [];

    /// <summary>Called with the recipient and the number of this send (1-based) before it counts as sent.</summary>
    public Action<string, int>? OnSend { get; set; }

    public Task<INotificationBatch> BeginBatchAsync(
        Stream? protocolLog = null,
        CancellationToken ct = default
    ) => Task.FromResult<INotificationBatch>(new Batch(this));

    public IReadOnlyList<FailureDetail> DescribeFailure(Exception ex) => [];

    private sealed class Batch(FakeNotificationChannel channel) : INotificationBatch
    {
        public Task SendAsync(
            string recipient,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        )
        {
            channel.OnSend?.Invoke(recipient, ++channel._sends);
            channel.SentTo.Add(recipient);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
