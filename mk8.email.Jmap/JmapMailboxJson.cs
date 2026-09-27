using System.Text.Json.Nodes;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal static class JmapMailboxJson
{
    public static readonly HashSet<string> Properties = new HashSet<string>(
        [
            "id",
            "name",
            "parentId",
            "role",
            "sortOrder",
            "totalEmails",
            "unreadEmails",
            "totalThreads",
            "unreadThreads",
            "myRights",
            "isSubscribed",
        ],
        StringComparer.Ordinal);

    public static JsonObject Build(
        JmapMailboxView mailbox,
        IReadOnlySet<string>? properties = null)
    {
        var result = new JsonObject { ["id"] = JmapId.Mailbox(mailbox.Id) };
        if (Wants("name")) result["name"] = mailbox.Name;
        if (Wants("parentId")) result["parentId"] = mailbox.ParentId is null
            ? null
            : JmapId.Mailbox(mailbox.ParentId.Value);
        if (Wants("role")) result["role"] = mailbox.Role;
        if (Wants("sortOrder")) result["sortOrder"] = mailbox.SortOrder;
        if (Wants("totalEmails")) result["totalEmails"] = mailbox.TotalEmails;
        if (Wants("unreadEmails")) result["unreadEmails"] = mailbox.UnreadEmails;
        if (Wants("totalThreads")) result["totalThreads"] = mailbox.TotalThreads;
        if (Wants("unreadThreads")) result["unreadThreads"] = mailbox.UnreadThreads;
        if (Wants("myRights"))
        {
            result["myRights"] = new JsonObject
            {
                ["mayReadItems"] = true,
                ["mayAddItems"] = true,
                ["mayRemoveItems"] = true,
                ["maySetSeen"] = true,
                ["maySetKeywords"] = true,
                ["mayCreateChild"] = true,
                ["mayRename"] = !mailbox.IsProtected,
                ["mayDelete"] = !mailbox.IsProtected,
                ["maySubmit"] = true,
            };
        }
        if (Wants("isSubscribed")) result["isSubscribed"] = mailbox.IsSubscribed;
        return result;

        bool Wants(string name) => properties is null || properties.Contains(name);
    }
}
