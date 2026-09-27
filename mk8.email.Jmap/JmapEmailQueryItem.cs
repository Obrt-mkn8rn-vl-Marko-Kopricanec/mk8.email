using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Application.Protocol;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed record JmapEmailQueryItem(
    EmailDB Email,
    MimeMessage Message,
    long Size,
    IReadOnlySet<string> Keywords,
    string ThreadId,
    bool HasAttachment,
    string FromSortValue,
    string ToSortValue,
    string SubjectSortValue,
    DateTimeOffset? SentAtSortValue) : IDisposable
{
    public void Dispose() => Message.Dispose();
}
