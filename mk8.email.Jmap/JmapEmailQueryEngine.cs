using Microsoft.EntityFrameworkCore;
using MimeKit;
using System.Text.RegularExpressions;
using mk8.email.Application.Protocol;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal static partial class JmapEmailQueryEngine
{
    public static async Task<List<JmapEmailQueryItem>> LoadAsync(
        EmailDbContext database,
        MailboxMessageContentService content,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var emails = await database.Emails.AsNoTracking()
            .Where(email => email.Folder.InboxId == accountId && !email.IsDeleted)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<JmapEmailQueryItem>(emails.Count);
        try
        {
            for (var emailIndex = 0; emailIndex < emails.Count; emailIndex++)
            {
                var email = emails[emailIndex];
                var rawMessage = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
                var message = JmapEmailCodec.Parse(rawMessage);
                var keywords = JmapEmailCodec.BuildKeywords(email)
                    .Select(item => item.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                result.Add(new JmapEmailQueryItem(
                    email,
                    message,
                    email.SizeBytes > 0 ? email.SizeBytes : rawMessage.LongLength,
                    keywords,
                    JmapId.Thread(email.ThreadObjectId ?? email.Id.ToString("N")),
                    JmapEmailCodec.HasAttachment(message),
                    JmapEmailCodec.FirstAddressInLastHeader(message, "From"),
                    JmapEmailCodec.FirstAddressInLastHeader(message, "To"),
                    JmapEmailCodec.LastTextHeader(message, "Subject"),
                    JmapEmailCodec.LastDateHeader(message, "Date")));
            }
            return result;
        }
        catch
        {
            for (var itemIndex = 0; itemIndex < result.Count; itemIndex++)
                result[itemIndex].Dispose();
            throw;
        }
    }

    internal static string BaseSubject(string? value) => Rfc5256.BaseSubject(value);

    private static string AllSearchableText(JmapEmailQueryItem item) => string.Join(
        '\n',
        HeaderText(item.Message, "From"),
        HeaderText(item.Message, "To"),
        HeaderText(item.Message, "Cc"),
        HeaderText(item.Message, "Bcc"),
        HeaderText(item.Message, "Subject"),
        BodyText(item));

    private static string HeaderText(MimeMessage message, string name) =>
        JmapEmailCodec.SearchableHeaderText(message, name);

    private static string BodyText(JmapEmailQueryItem item) =>
        JmapEmailCodec.SearchableBodyText(item.Message);

    private static bool MatchesText(string haystack, string query)
    {
        foreach (var token in SearchTokenRegex().Matches(query).Select(match => match.Value))
        {
            var normalized = token.Length >= 2 && token[0] is '\'' or '"' && token[^1] == token[0]
                ? token[1..^1]
                : token;
            normalized = normalized.Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\'", "'", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
            if (normalized.Length > 0
                && !haystack.Contains(normalized, StringComparison.InvariantCultureIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool MatchesHeader(MimeMessage message, string name, string? text)
    {
        var headers = JmapEmailCodec.MessageHeaders(message)
            .Where(header => header.Field.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return headers.Length > 0
            && (text is null || headers.Any(header => MatchesText(header.Value, text)));
    }

    [GeneratedRegex("(?:\\\"(?:\\\\.|[^\\\"])*\\\"|'(?:\\\\.|[^'])*'|\\S+)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SearchTokenRegex();
}
