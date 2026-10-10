using mk8.email.Contracts.Messaging;
using mk8.email.MailWire;
using mk8.email.Messaging;

namespace mk8.email.Messaging.Tests;

internal sealed class NativeSmtpJournalControl(IGatewayTrafficJournal inner, NativeSmtpJournalMode mode) : IGatewayTrafficJournal
{
    private readonly Lock _sync = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Guid? _enqueueRequest;
    private bool _refused;
    public TaskCompletionSource AcceptanceHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Guid? EnqueueRequest { get { lock (_sync) return _enqueueRequest; } }
    public bool Refused { get { lock (_sync) return _refused; } }

    public async Task AppendAsync(GatewayTrafficRecord record, CancellationToken cancellationToken = default)
    {
        bool refuse;
        lock (_sync)
        {
            if (string.Equals(record.Direction, GatewayTrafficDirections.Inbound, StringComparison.Ordinal)
                && record.Metadata.TryGetValue("operation", out var operation)
                && string.Equals(operation, ApplicationOperations.SmtpEnqueue, StringComparison.Ordinal))
            {
                _enqueueRequest = record.ApplicationRequestId;
            }
            refuse = !_refused && _enqueueRequest is not null
                && record.ApplicationRequestId == _enqueueRequest
                && ((mode == NativeSmtpJournalMode.RefuseEnqueueInput && string.Equals(record.Direction, GatewayTrafficDirections.Inbound, StringComparison.Ordinal))
                    || (mode == NativeSmtpJournalMode.RefuseEnqueueOutput && string.Equals(record.Direction, GatewayTrafficDirections.Outbound, StringComparison.Ordinal)));
            if (refuse) _refused = true;
        }
        if (refuse) throw new IOException("Controlled SMTP enqueue journal refusal.");
        await inner.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        if (mode == NativeSmtpJournalMode.HoldNativeAcceptance
            && string.Equals(record.Direction, GatewayTrafficDirections.Outbound, StringComparison.Ordinal)
            && string.Equals(record.ContentType, "application/octet-stream", StringComparison.Ordinal)
            && MailWireEncoding.Instance.GetString(record.Payload).StartsWith("250 2.0.0 Queued as ", StringComparison.Ordinal))
        {
            AcceptanceHeld.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        inner.ReadSessionAsync(sessionId, cancellationToken);

    public void Release() => _release.TrySetResult();
}
