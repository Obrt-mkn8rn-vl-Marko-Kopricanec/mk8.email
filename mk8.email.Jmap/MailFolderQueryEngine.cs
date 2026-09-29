using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static class MailFolderQueryEngine
{
    public static IReadOnlyList<JmapMailboxView> Filter(
        IReadOnlyList<JmapMailboxView> all,
        MailFolderFilter? filter,
        bool filterAsTree)
    {
        var predicate = BuildPredicate(filter);
        var matches = all.Where(predicate).ToDictionary(folder => folder.Id);
        if (filterAsTree)
        {
            foreach (var folder in matches.Values.ToArray())
            {
                var parentId = folder.ParentId;
                var visited = new HashSet<Guid>();
                while (parentId is not null)
                {
                    if (!visited.Add(parentId.Value)
                        || !matches.TryGetValue(parentId.Value, out var parent))
                    {
                        matches.Remove(folder.Id);
                        break;
                    }
                    parentId = parent.ParentId;
                }
            }
        }
        return matches.Values.ToArray();
    }

    public static IReadOnlyList<JmapMailboxView> Sort(
        IReadOnlyList<JmapMailboxView> all,
        IReadOnlyList<JmapMailboxView> filtered,
        IReadOnlyList<MailFolderSort> comparators,
        bool sortAsTree)
    {
        if (comparators is null || comparators.Any(item => item is null
            || !Enum.IsDefined(item.Field) || !Enum.IsDefined(item.Collation)))
            throw new InvalidOperationException("The folder query sort is invalid.");
        var comparer = Comparer<JmapMailboxView>.Create((left, right) => Compare(left, right, comparators));
        if (!sortAsTree)
            return filtered.Order(comparer).ToArray();
        var included = filtered.Select(folder => folder.Id).ToHashSet();
        var byParent = all.ToLookup(folder => folder.ParentId);
        var ordered = new List<JmapMailboxView>(all.Count);
        var seen = new HashSet<Guid>();

        void Visit(JmapMailboxView folder)
        {
            if (!seen.Add(folder.Id)) return;
            if (included.Contains(folder.Id)) ordered.Add(folder);
            foreach (var child in byParent[folder.Id].Order(comparer))
                Visit(child);
        }

        foreach (var root in byParent[null].Order(comparer))
            Visit(root);
        foreach (var orphan in all.Where(folder => !seen.Contains(folder.Id)).Order(comparer))
            Visit(orphan);
        return ordered;
    }

    private static Func<JmapMailboxView, bool> BuildPredicate(MailFolderFilter? filter, int depth = 0)
    {
        if (filter is null) return static _ => true;
        if (depth > 64 || !Enum.IsDefined(filter.Operator))
            throw new InvalidOperationException("The folder query filter is invalid.");
        if (filter.Operator != MailFolderFilterOperator.Condition)
        {
            if (filter.Conditions is null)
                throw new InvalidOperationException("The folder query filter has no conditions.");
            var children = filter.Conditions.Select(item => BuildPredicate(item, depth + 1)).ToArray();
            return filter.Operator switch
            {
                MailFolderFilterOperator.And => folder => children.All(child => child(folder)),
                MailFolderFilterOperator.Or => folder => children.Any(child => child(folder)),
                _ => folder => children.All(child => !child(folder)),
            };
        }
        if (filter.Conditions is not null || !Enum.IsDefined(filter.ParentConstraint)
            || filter.ParentConstraint == MailFolderParentConstraint.Folder && filter.ParentId is null
            || filter.ParentConstraint != MailFolderParentConstraint.Folder && filter.ParentId is not null)
            throw new InvalidOperationException("The folder query condition is invalid.");
        return folder => (filter.ParentConstraint switch
        {
            MailFolderParentConstraint.Any => true,
            MailFolderParentConstraint.Root => folder.ParentId is null,
            MailFolderParentConstraint.Folder => folder.ParentId == filter.ParentId,
            _ => false,
        })
            && (filter.Name is null || folder.Name.Contains(filter.Name, StringComparison.InvariantCultureIgnoreCase))
            && (!filter.MatchNullRole || folder.Role is null)
            && (filter.Role is null || string.Equals(folder.Role, filter.Role, StringComparison.Ordinal))
            && (filter.HasAnyRole is null || (folder.Role is not null) == filter.HasAnyRole)
            && (filter.IsSubscribed is null || folder.IsSubscribed == filter.IsSubscribed);
    }

    private static int Compare(JmapMailboxView left, JmapMailboxView right,
        IReadOnlyList<MailFolderSort> comparators)
    {
        foreach (var comparator in comparators)
        {
            var comparison = comparator.Field switch
            {
                MailFolderSortField.SortOrder => left.SortOrder.CompareTo(right.SortOrder),
                MailFolderSortField.Name => JmapCollation.Compare(left.Name, right.Name, comparator.Collation),
                _ => 0,
            };
            if (comparison != 0)
                return comparator.IsAscending ? comparison : -comparison;
        }
        return left.Id.CompareTo(right.Id);
    }
}
