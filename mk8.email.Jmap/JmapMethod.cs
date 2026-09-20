using System.Text.Json.Nodes;
using mk8.email.Application.Interfaces;

namespace mk8.email.Jmap;

public sealed class JmapInvocationContext(
    AuthenticatedMailUser user,
    IReadOnlySet<string> capabilities,
    IDictionary<string, string> createdIds)
{
    private readonly List<Func<CancellationToken, Task>> _postCommitActions = [];

    public AuthenticatedMailUser User { get; } = user;
    public IReadOnlySet<string> Capabilities { get; } = capabilities;
    public IDictionary<string, string> CreatedIds { get; } = createdIds;

    public string? ResolveId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id[0] != '#')
            return id;

        return CreatedIds.TryGetValue(id[1..], out var resolved) ? resolved : null;
    }

    internal int MarkPostCommitActions() => _postCommitActions.Count;

    internal void AddPostCommitAction(Func<CancellationToken, Task> action) =>
        _postCommitActions.Add(action);

    internal IReadOnlyList<Func<CancellationToken, Task>> TakePostCommitActions(int marker)
    {
        var actions = _postCommitActions.Skip(marker).ToArray();
        _postCommitActions.RemoveRange(marker, _postCommitActions.Count - marker);
        return actions;
    }

    internal void DiscardPostCommitActions(int marker) =>
        _postCommitActions.RemoveRange(marker, _postCommitActions.Count - marker);
}

public sealed record JmapMethodResponse(
    string Name,
    JsonObject Arguments,
    IReadOnlyList<JmapMethodResponse>? AdditionalResponses = null)
{
    public static JmapMethodResponse Error(string type, string? description = null)
    {
        var arguments = new JsonObject { ["type"] = type };
        if (!string.IsNullOrEmpty(description))
            arguments["description"] = description;
        return new JmapMethodResponse("error", arguments);
    }
}

public interface IJmapMethod
{
    string Name { get; }
    string Capability { get; }

    Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken);
}

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
