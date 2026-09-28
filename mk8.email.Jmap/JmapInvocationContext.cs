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
    internal Dictionary<string, string> ReferenceAliases { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? ResolveId(string? id)
    {
        if (id is null || !ReferenceAliases.TryGetValue(id, out var creationKey))
            return id;

        return CreatedIds.TryGetValue(creationKey, out var resolved) ? resolved : null;
    }

    internal bool TryGetReferenceKey(string value, out string creationKey) =>
        ReferenceAliases.TryGetValue(value, out creationKey!);

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
