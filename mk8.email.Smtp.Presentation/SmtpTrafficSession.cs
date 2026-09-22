using System.Globalization;
using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;

namespace mk8.email.Smtp.Presentation;

internal sealed class SmtpTrafficSession(
    IGatewayTrafficJournal journal,
    MailExchangeEndpoint endpoint,
    Guid applicationRequestId)
{
    private readonly Guid _sessionId = Guid.CreateVersion7();
    private long _sequence;

    public Task RecordAsync(
        string direction,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken) =>
        journal.AppendAsync(
            new GatewayTrafficRecord(
                Guid.CreateVersion7(),
                _sessionId,
                Interlocked.Increment(ref _sequence) - 1,
                direction,
                SmtpPresentationOperations.Protocol,
                "application/octet-stream",
                payload.ToArray(),
                new Dictionary<string, string>
                {
                    ["remoteHost"] = endpoint.Host,
                    ["remotePort"] = endpoint.Port.ToString(CultureInfo.InvariantCulture),
                },
                DateTimeOffset.UtcNow,
                applicationRequestId),
            cancellationToken);
}
