using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

public sealed record JmapAccount(
    Guid InboxId,
    Guid UserId,
    string Username,
    string Address,
    long QuotaBytes);
