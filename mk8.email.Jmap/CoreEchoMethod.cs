using mk8.email.Contracts.Messaging;
using System.Text.Json.Nodes;
using mk8.email.Application.Interfaces;

namespace mk8.email.Jmap;

public sealed class CoreEchoMethod : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.Echo;
    public string Capability => JmapConstants.CoreCapability;

    public Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken) =>
        Task.FromResult(new JmapMethodResponse(Operation, (JsonObject)arguments.DeepClone()));
}
