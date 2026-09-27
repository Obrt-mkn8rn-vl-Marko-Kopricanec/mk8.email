using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MimeKit;
using MimeKit.Utils;

namespace mk8.email.Jmap;

internal sealed record JmapBuiltMessage(
    MimeMessage Message,
    byte[] RawBytes,
    string Sender,
    string Recipient,
    string? Cc,
    string Subject,
    string MessageId,
    string? InReplyTo);
