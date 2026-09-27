using System.Text.Json.Nodes;
using mk8.email.Application.Interfaces;

namespace mk8.email.Jmap;

public interface IJmapMethod
{
    string Name { get; }
    string Capability { get; }

    Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken);
}
