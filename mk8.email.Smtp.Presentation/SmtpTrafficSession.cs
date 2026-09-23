using System.Globalization;
using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;

namespace mk8.email.Smtp.Presentation;

internal sealed class SmtpTrafficSession
{
    private readonly IGatewayTrafficJournal _journal;
    private readonly IReadOnlyDictionary<string, string> _metadata;
    private readonly Guid? _applicationRequestId;
    private readonly Guid _sessionId = Guid.CreateVersion7();
    private long _sequence;

    public SmtpTrafficSession(
        IGatewayTrafficJournal journal,
        MailExchangeEndpoint endpoint,
        Guid applicationRequestId)
    {
        _journal = journal;
        _applicationRequestId = applicationRequestId;
        _metadata = new Dictionary<string, string>
        {
            ["remoteHost"] = endpoint.Host,
            ["remotePort"] = endpoint.Port.ToString(CultureInfo.InvariantCulture),
        };
    }

    public SmtpTrafficSession(
        IGatewayTrafficJournal journal,
        string remoteEndpoint,
        int localPort)
    {
        _journal = journal;
        _metadata = new Dictionary<string, string>
        {
            ["remoteEndpoint"] = remoteEndpoint,
            ["listenerPort"] = localPort.ToString(CultureInfo.InvariantCulture),
        };
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
                SmtpPresentationOperations.Protocol,
                "application/octet-stream",
                payload.ToArray(),
                _metadata,
                DateTimeOffset.UtcNow,
                _applicationRequestId),
            cancellationToken);
}
