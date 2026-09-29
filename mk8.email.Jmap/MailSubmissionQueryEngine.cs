using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class MailSubmissionQueryEngine
{
    public static Func<JmapEmailSubmissionDB, bool> BuildPredicate(MailSubmissionFilter? filter,
        int depth = 0)
    {
        if (filter is null) return static _ => true;
        if (depth > 64 || !Enum.IsDefined(filter.Operator))
            throw new InvalidOperationException("The submission query filter is invalid.");
        if (filter.Operator != MailSubmissionFilterOperator.Condition)
        {
            if (filter.Conditions is null)
                throw new InvalidOperationException("The submission query filter has no conditions.");
            var children = filter.Conditions.Select(item => BuildPredicate(item, depth + 1)).ToArray();
            return filter.Operator switch
            {
                MailSubmissionFilterOperator.And => item => children.All(child => child(item)),
                MailSubmissionFilterOperator.Or => item => children.Any(child => child(item)),
                _ => item => children.All(child => !child(item)),
            };
        }
        if (filter.Conditions is not null)
            throw new InvalidOperationException("The submission query condition contains nested filters.");
        var identities = filter.IdentityIds?.ToHashSet(StringComparer.Ordinal);
        var emails = filter.EmailIds?.ToHashSet(StringComparer.Ordinal);
        var threads = filter.ThreadIds?.ToHashSet(StringComparer.Ordinal);
        return item => (identities is null || identities.Contains(item.IdentityId))
            && (emails is null || emails.Contains(item.EmailId))
            && (threads is null || threads.Contains(item.ThreadId))
            && (filter.UndoStatus is null || string.Equals(item.UndoStatus, filter.UndoStatus, StringComparison.Ordinal))
            && (filter.Before is null || item.SendAt < filter.Before.Value)
            && (filter.After is null || item.SendAt >= filter.After.Value);
    }

    public static IReadOnlyList<JmapEmailSubmissionDB> Sort(IEnumerable<JmapEmailSubmissionDB> values,
        IReadOnlyList<MailSubmissionSort> comparators)
    {
        if (comparators is null || comparators.Any(item => item is null
            || !Enum.IsDefined(item.Field) || !Enum.IsDefined(item.Collation)))
            throw new InvalidOperationException("The submission query sort is invalid.");
        var comparer = Comparer<JmapEmailSubmissionDB>.Create((left, right) =>
        {
            foreach (var comparator in comparators)
            {
                var comparison = comparator.Field switch
                {
                    MailSubmissionSortField.EmailId => JmapCollation.Compare(left.EmailId, right.EmailId,
                        comparator.Collation),
                    MailSubmissionSortField.ThreadId => JmapCollation.Compare(left.ThreadId, right.ThreadId,
                        comparator.Collation),
                    MailSubmissionSortField.SentAt => left.SendAt.CompareTo(right.SendAt),
                    _ => 0,
                };
                if (comparison != 0)
                    return comparator.IsAscending ? comparison : -comparison;
            }
            return left.Id.CompareTo(right.Id);
        });
        return values.Order(comparer).ToArray();
    }
}
