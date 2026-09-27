using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class JmapEmailMutationHelpers
{
    public static async Task<(FolderDB? Folder, JsonObject? Error)> ResolveMailboxAsync(
        EmailDbContext database,
        Guid accountId,
        JsonNode? node,
        JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (node is not JsonObject map)
            return (null, JmapMethodHelpers.SetError("invalidProperties", properties: ["mailboxIds"]));
        if (map.Count > 1)
            return (null, JmapMethodHelpers.SetError("tooManyMailboxes"));
        if (map.Count == 0)
            return (null, JmapMethodHelpers.SetError("invalidProperties", properties: ["mailboxIds"]));
        var item = map.Single();
        if (item.Value is not JsonValue value
            || !value.TryGetValue<bool>(out var enabled)
            || !enabled
            || !JmapId.TryParseMailbox(context.ResolveId(item.Key), out var folderId))
        {
            return (null, JmapMethodHelpers.SetError("invalidProperties", properties: ["mailboxIds"]));
        }
        var folder = await database.Folders
            .SingleOrDefaultAsync(candidate => candidate.Id == folderId
                && candidate.InboxId == accountId,
                cancellationToken).ConfigureAwait(false);
        return folder is null
            ? (null, JmapMethodHelpers.SetError("invalidProperties", properties: ["mailboxIds"]))
            : (folder, null);
    }

    public static JsonObject CreatedEmail(EmailDB email) => new()
    {
        ["id"] = JmapId.Email(email.Id),
        ["blobId"] = JmapId.RawBlob(email.Id),
        ["threadId"] = JmapId.Thread(email.ThreadObjectId ?? email.Id.ToString("N")),
        ["size"] = email.SizeBytes,
    };

    public static bool TryGetObjectMap(
        JsonObject arguments,
        string name,
        bool required,
        out IReadOnlyDictionary<string, JsonObject>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null)
            return !required;
        if (node is not JsonObject map)
            return false;
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in map)
        {
            if (item.Value is not JsonObject value)
                return false;
            result[item.Key] = value;
        }
        values = result;
        return true;
    }

    public static DateTime DetermineReceivedAt(byte[] raw)
    {
        try
        {
            using var message = JmapEmailCodec.Parse(raw);
            foreach (var header in message.Headers.Where(header =>
                         header.Field.Equals("Received", StringComparison.OrdinalIgnoreCase)))
            {
                var separator = header.Value.LastIndexOf(';');
                if (separator >= 0
                    && MimeKit.Utils.DateUtils.TryParse(
                        header.Value[(separator + 1)..],
                        out var date))
                {
                    return date.UtcDateTime;
                }
            }
            return DateTime.UtcNow;
        }
        catch (FormatException)
        {
            return DateTime.UtcNow;
        }
    }
}
