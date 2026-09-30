using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class MailMessageFlagMutations
{
    public static IReadOnlyList<MailMessageFlagEntry>? Apply(MailMessagePatch patch, MailMessageFlagField field,
        IEnumerable<string> current)
    {
        var values = current.ToDictionary(key => key, _ => MailMessageFlagValue.Enabled, StringComparer.Ordinal);
        var invalid = false;
        foreach (var change in patch.Flags.Where(change => change.Field == field))
        {
            switch (change.Kind)
            {
                case MailMessageFlagChangeKind.Clear:
                    values.Clear();
                    invalid = false;
                    break;
                case MailMessageFlagChangeKind.Replace:
                    values = change.Entries.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                    invalid = change.InvalidContainer;
                    break;
                case MailMessageFlagChangeKind.Set:
                    values[change.Entries[0].Key] = change.Entries[0].Value;
                    break;
                case MailMessageFlagChangeKind.Remove:
                    values.Remove(change.Entries[0].Key);
                    break;
                default: throw new InvalidOperationException("The message flag change is invalid.");
            }
        }
        return invalid ? null : values.Select(item => new MailMessageFlagEntry(item.Key, item.Value)).ToArray();
    }

    public static HashSet<string> Keywords(EmailDB email)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (email.IsRead) values.Add("$seen");
        if (email.IsFlagged) values.Add("$flagged");
        if (email.IsDraft) values.Add("$draft");
        if (email.IsAnswered) values.Add("$answered");
        foreach (var keyword in email.Keywords ?? [])
        {
            var normalized = keyword.ToProtocolLowerInvariant();
            if (JmapEmailCodec.IsValidKeyword(normalized)) values.Add(normalized);
        }
        return values;
    }

    public static (HashSet<string> Values, MailMessageMutationFailure? Failure) ReadKeywords(
        IReadOnlyList<MailMessageFlagEntry>? entries)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (entries is null) return (values, Invalid());
        foreach (var entry in entries)
        {
            var normalized = entry.Key.ToProtocolLowerInvariant();
            if (entry.Value != MailMessageFlagValue.Enabled || !JmapEmailCodec.IsValidKeyword(normalized))
                return (values, Invalid());
            values.Add(normalized);
        }
        return values.Count > 128 ? (values, new(MailMessageMutationError.TooManyKeywords, null, null, null)) : (values, null);
    }

    private static MailMessageMutationFailure Invalid() => new(MailMessageMutationError.InvalidProperties, null, null, null);
}
