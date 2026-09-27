using System.Text.Json.Nodes;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

public sealed class JmapInvocationContext(
    AuthenticatedMailUser user,
    IReadOnlySet<MailFeature> features,
    IDictionary<string, string> createdIds)
{
    private readonly List<Func<CancellationToken, Task>> _postCommitActions = [];
    private readonly List<mk8.email.Contracts.Messaging.ApplicationRequest> _presentationEffects = [];

    public AuthenticatedMailUser User { get; } = user;
    public IReadOnlySet<MailFeature> Features { get; } = features;
    public IDictionary<string, string> CreatedIds { get; } = createdIds;

    public string? ResolveId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id[0] != '#')
            return id;

        return CreatedIds.TryGetValue(id[1..], out var resolved) ? resolved : null;
    }

    internal int MarkPostCommitActions() => _postCommitActions.Count;

    internal int MarkPresentationEffects() => _presentationEffects.Count;

    internal void AddPresentationEffect(mk8.email.Contracts.Messaging.ApplicationRequest effect) =>
        _presentationEffects.Add(effect);

    internal IReadOnlyList<mk8.email.Contracts.Messaging.ApplicationRequest> PresentationEffectsSince(int marker) =>
        _presentationEffects.Skip(marker).ToArray();

    internal void DiscardPresentationEffects(int marker) =>
        _presentationEffects.RemoveRange(marker, _presentationEffects.Count - marker);

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
