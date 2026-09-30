using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static class MailMessagePatchMerger
{
    public static MailMessagePatch Merge(IReadOnlyList<MailMessagePatchFragment> fragments)
    {
        var entries = new Dictionary<string, MailMessagePatchFragment>(StringComparer.Ordinal);
        foreach (var fragment in fragments) entries[fragment.CorrelationKey] = fragment;
        var active = entries.Values.ToArray();
        var mime = active.Any(item => item.Patch.RequiresMime);
        var failure = DeferredFailure(active);
        if (failure is not null) return new([], [], [], 256, mime, failure);
        foreach (var left in active)
            foreach (var right in active)
                if (Prefix(left.CollisionPath, right.CollisionPath))
                    return new([], [], [], 256, mime, new(MailMessageMutationError.InvalidPatch, null, null, null));
        var parts = active.SelectMany(item => item.Patch.PartFields)
            .DistinctBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var assertions = active.SelectMany(item => item.Patch.Assertions).GroupBy(item => item.Label, StringComparer.Ordinal)
            .Select(group => group.First() with
            {
                Changes = group.SelectMany(item => item.Changes).ToArray(),
                MatchVisibleTextValues = group.Any(item => item.MatchVisibleTextValues),
            }).ToArray();
        return new(active.SelectMany(item => item.Patch.Flags).ToArray(), assertions, parts, 256, mime, null);
    }

    private static MailMessageMutationFailure? DeferredFailure(MailMessagePatchFragment[] entries)
    {
        var failed = entries.Where(item => item.Stage != MailMessageFragmentStage.Ok).ToArray();
        if (failed.Length == 0) return null;
        var stage = failed.Min(item => item.Stage);
        var chosen = failed.Where(item => item.Stage == stage).ToArray();
        if (stage == MailMessageFragmentStage.UnknownObservation)
            return new(MailMessageMutationError.InvalidProperties, null,
                chosen.SelectMany(item => item.Patch.Failure!.Properties ?? []).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray(), null);
        return chosen[0].Patch.Failure;
    }

    private static bool Prefix(IReadOnlyList<string> first, IReadOnlyList<string> second) =>
        first.Count < second.Count && first.SequenceEqual(second.Take(first.Count), StringComparer.Ordinal);
}
