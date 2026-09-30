using System.Diagnostics.CodeAnalysis;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

[SuppressMessage("Style", "MA0048", Justification = "This partial query engine is split by typed filter policy and MIME loading responsibilities.")]
internal static partial class JmapEmailQueryEngine
{
    public static List<JmapEmailQueryItem> Filter(
        IReadOnlyList<JmapEmailQueryItem> all, MailMessageFilter? filter)
    {
        var byThread = all.ToLookup(item => item.ThreadId, StringComparer.Ordinal);
        return all.Where(item => MatchesFilter(item, filter, byThread)).ToList();
    }

    public static List<JmapEmailQueryItem> Sort(
        IReadOnlyList<JmapEmailQueryItem> all,
        IReadOnlyList<JmapEmailQueryItem> filtered,
        IReadOnlyList<MailMessageSort> comparators)
    {
        var byThread = all.ToLookup(item => item.ThreadId, StringComparer.Ordinal);
        var comparer = Comparer<JmapEmailQueryItem>.Create((left, right) =>
        {
            foreach (var comparator in comparators)
            {
                var comparison = Compare(left, right, comparator, byThread);
                if (comparison != 0)
                    return comparator.IsAscending ? comparison : -comparison;
            }
            return left.Email.Id.CompareTo(right.Email.Id);
        });
        return filtered.Order(comparer).ToList();
    }

    public static bool UsesMutableFilter(MailMessageFilter? filter) =>
        FilterUsesAnyField(filter, field => field is MailMessageFilterField.InMailbox
            or MailMessageFilterField.InMailboxOtherThan
            or MailMessageFilterField.AllInThreadHaveKeyword
            or MailMessageFilterField.SomeInThreadHaveKeyword
            or MailMessageFilterField.NoneInThreadHaveKeyword
            or MailMessageFilterField.HasKeyword
            or MailMessageFilterField.NotKeyword);

    public static bool UsesThreadProperties(
        MailMessageFilter? filter, IReadOnlyList<MailMessageSort> sort) =>
        FilterUsesAnyField(filter, field => field is MailMessageFilterField.AllInThreadHaveKeyword
            or MailMessageFilterField.SomeInThreadHaveKeyword
            or MailMessageFilterField.NoneInThreadHaveKeyword)
        || sort.Any(comparator => comparator.Field is MailMessageSortField.AllInThreadHaveKeyword
            or MailMessageSortField.SomeInThreadHaveKeyword);

    public static bool UsesMutableSort(IReadOnlyList<MailMessageSort> sort) =>
        sort.Any(comparator => comparator.Field is MailMessageSortField.HasKeyword
            or MailMessageSortField.AllInThreadHaveKeyword
            or MailMessageSortField.SomeInThreadHaveKeyword);

    private static bool FilterUsesAnyField(
        MailMessageFilter? filter, Func<MailMessageFilterField, bool> predicate) =>
        filter is not null && (filter.Operator == MailMessageFilterOperator.Condition
            ? filter.Terms?.Any(term => predicate(term.Field)) == true
            : filter.Conditions?.Any(child => FilterUsesAnyField(child, predicate)) == true);

    private static bool MatchesFilter(
        JmapEmailQueryItem item,
        MailMessageFilter? filter,
        ILookup<string, JmapEmailQueryItem> byThread)
    {
        if (filter is null) return true;
        return filter.Operator switch
        {
            MailMessageFilterOperator.And => filter.Conditions!.All(child => MatchesFilter(item, child, byThread)),
            MailMessageFilterOperator.Or => filter.Conditions!.Any(child => MatchesFilter(item, child, byThread)),
            MailMessageFilterOperator.Not => filter.Conditions!.All(child => !MatchesFilter(item, child, byThread)),
            _ => filter.Terms!.All(term => MatchesTerm(item, term, byThread)),
        };
    }

    private static bool MatchesTerm(
        JmapEmailQueryItem item,
        MailMessageFilterTerm term,
        ILookup<string, JmapEmailQueryItem> byThread)
    {
        var value = term.Text;
        return term.Field switch
        {
            MailMessageFilterField.InMailbox => string.Equals(
                JmapId.Mailbox(item.Email.FolderId), value, StringComparison.Ordinal),
            MailMessageFilterField.InMailboxOtherThan =>
                !term.Values!.Contains(JmapId.Mailbox(item.Email.FolderId), StringComparer.Ordinal),
            MailMessageFilterField.Before => item.Email.ReceivedAt.ToUniversalTime() < term.UtcDate!.Value,
            MailMessageFilterField.After => item.Email.ReceivedAt.ToUniversalTime() >= term.UtcDate!.Value,
            MailMessageFilterField.MinSize => item.Size >= term.Number!.Value,
            MailMessageFilterField.MaxSize => item.Size < term.Number!.Value,
            MailMessageFilterField.AllInThreadHaveKeyword =>
                byThread[item.ThreadId].All(threadItem => threadItem.Keywords.Contains(value!)),
            MailMessageFilterField.SomeInThreadHaveKeyword =>
                byThread[item.ThreadId].Any(threadItem => threadItem.Keywords.Contains(value!)),
            MailMessageFilterField.NoneInThreadHaveKeyword =>
                byThread[item.ThreadId].All(threadItem => !threadItem.Keywords.Contains(value!)),
            MailMessageFilterField.HasKeyword => item.Keywords.Contains(value!),
            MailMessageFilterField.NotKeyword => !item.Keywords.Contains(value!),
            MailMessageFilterField.HasAttachment => item.HasAttachment == term.Flag!.Value,
            MailMessageFilterField.Text => MatchesText(AllSearchableText(item), value!),
            MailMessageFilterField.From => MatchesText(HeaderText(item.Message, "From"), value!),
            MailMessageFilterField.To => MatchesText(HeaderText(item.Message, "To"), value!),
            MailMessageFilterField.Cc => MatchesText(HeaderText(item.Message, "Cc"), value!),
            MailMessageFilterField.Bcc => MatchesText(HeaderText(item.Message, "Bcc"), value!),
            MailMessageFilterField.Subject => MatchesText(HeaderText(item.Message, "Subject"), value!),
            MailMessageFilterField.Body => MatchesText(BodyText(item), value!),
            MailMessageFilterField.Header => MatchesHeader(item.Message, value!, term.HeaderText),
            _ => false,
        };
    }

    private static int Compare(
        JmapEmailQueryItem left,
        JmapEmailQueryItem right,
        MailMessageSort comparator,
        ILookup<string, JmapEmailQueryItem> byThread) => comparator.Field switch
        {
            MailMessageSortField.ReceivedAt => left.Email.ReceivedAt.CompareTo(right.Email.ReceivedAt),
            MailMessageSortField.Size => left.Size.CompareTo(right.Size),
            MailMessageSortField.From => JmapCollation.Compare(
                left.FromSortValue, right.FromSortValue, comparator.Collation),
            MailMessageSortField.To => JmapCollation.Compare(
                left.ToSortValue, right.ToSortValue, comparator.Collation),
            MailMessageSortField.Subject => JmapCollation.Compare(
                BaseSubject(left.SubjectSortValue), BaseSubject(right.SubjectSortValue), comparator.Collation),
            MailMessageSortField.SentAt => Nullable.Compare(left.SentAtSortValue, right.SentAtSortValue),
            MailMessageSortField.HasKeyword => left.Keywords.Contains(comparator.Keyword!)
                .CompareTo(right.Keywords.Contains(comparator.Keyword!)),
            MailMessageSortField.AllInThreadHaveKeyword =>
                byThread[left.ThreadId].All(item => item.Keywords.Contains(comparator.Keyword!))
                    .CompareTo(byThread[right.ThreadId].All(item => item.Keywords.Contains(comparator.Keyword!))),
            MailMessageSortField.SomeInThreadHaveKeyword =>
                byThread[left.ThreadId].Any(item => item.Keywords.Contains(comparator.Keyword!))
                    .CompareTo(byThread[right.ThreadId].Any(item => item.Keywords.Contains(comparator.Keyword!))),
            _ => 0,
        };
}
