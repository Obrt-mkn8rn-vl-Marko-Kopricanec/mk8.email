using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging;

public sealed class GatewayTrafficSession
{
    private readonly IGatewayTrafficJournal _journal;
    private readonly string _protocol;
    private readonly IReadOnlyDictionary<string, string> _metadata;
    private readonly Guid? _applicationRequestId;
    private readonly Guid _sessionId = Guid.CreateVersion7();
    private long _sequence;

    public GatewayTrafficSession(
        IGatewayTrafficJournal journal,
        string protocol,
        IReadOnlyDictionary<string, string> metadata,
        Guid? applicationRequestId = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        _protocol = protocol;
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _applicationRequestId = applicationRequestId;
    }

    public Task RecordAsync(
        string direction,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken) =>
        _journal.AppendAsync(
            new GatewayTrafficRecord(
                Guid.CreateVersion7(),
                _sessionId,
                Interlocked.Increment(ref _sequence) - 1,
                direction,
                _protocol,
                "application/octet-stream",
                payload.ToArray(),
                _metadata,
                DateTimeOffset.UtcNow,
                _applicationRequestId),
            cancellationToken);
}
