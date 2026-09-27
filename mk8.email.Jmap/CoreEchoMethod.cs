using System.Text.Json.Nodes;
using mk8.email.Application.Interfaces;

namespace mk8.email.Jmap;

public sealed class CoreEchoMethod : IJmapMethod
{
    public string Name => "Core/echo";
    public string Capability => JmapConstants.CoreCapability;

    public Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken) =>
        Task.FromResult(new JmapMethodResponse(Name, (JsonObject)arguments.DeepClone()));
}
