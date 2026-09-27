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
