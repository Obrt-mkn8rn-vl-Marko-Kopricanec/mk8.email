using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed record JmapMailboxView(
    Guid Id,
    string FullName,
    string Name,
    Guid? ParentId,
    string? Role,
    long SortOrder,
    bool IsSubscribed,
    int TotalEmails,
    int UnreadEmails,
    int TotalThreads,
    int UnreadThreads)
{
    public bool IsProtected => Role is "inbox" or "sent" or "drafts" or "trash" or "junk";
}
