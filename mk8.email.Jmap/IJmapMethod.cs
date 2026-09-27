using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

public interface IJmapMethod
{
    MailOperationKind Operation { get; }
    string Capability { get; }

    Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken);
}
