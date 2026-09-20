using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Utils;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class VacationResponder(
    EmailDbContext database,
    IEmailService emailService,
    IMailSubmissionQueue queue,
    EnvironmentConfig environment,
    TimeProvider timeProvider) : IVacationResponder
{
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromDays(7);
    private static readonly string[] RecipientHeaderFields =
    [
        "To",
        "Cc",
        "Bcc",
        "Resent-To",
        "Resent-Cc",
        "Resent-Bcc",
    ];

    public async Task<bool> QueueResponseAsync(
        string envelopeSender,
        string deliveredRecipient,
        string rawMessage,
        string targetFolder,
        Guid deliveryId,
        CancellationToken cancellationToken = default)
    {
        if (targetFolder != DefaultFolders.Inbox
            || string.IsNullOrWhiteSpace(envelopeSender)
            || !MailboxAddress.TryParse(envelopeSender, out var senderMailbox)
            || !string.Equals(senderMailbox.Address, envelopeSender, StringComparison.OrdinalIgnoreCase)
            || IsAutomatedAddress(senderMailbox.Address))
            return true;

        MimeMessage original;
        try
        {
            original = MimeMessage.Load(new MemoryStream(Encoding.Latin1.GetBytes(rawMessage)));
        }
        catch (FormatException)
        {
            return true;
        }
        using (original)
        {
            if (MustSuppress(original))
                return true;

            var route = await ResolveVacationRouteAsync(deliveredRecipient, cancellationToken);
            if (route is null
                || string.Equals(route.Address, senderMailbox.Address, StringComparison.OrdinalIgnoreCase)
                || !NamesRecipient(original, deliveredRecipient, route.Address))
                return true;
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var vacation = await database.JmapVacationResponses
                .AsNoTracking()
                .SingleOrDefaultAsync(response => response.AccountId == route.AccountId, cancellationToken);
            if (vacation is null
                || !vacation.IsEnabled
                || vacation.FromDate is not null && now < vacation.FromDate
                || vacation.ToDate is not null && now >= vacation.ToDate)
                return true;

            await using var transaction = await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var sent = await database.JmapVacationReplies.SingleOrDefaultAsync(
                reply => reply.AccountId == route.AccountId
                    && reply.SenderAddress == senderMailbox.Address.ToLower(),
                cancellationToken);
            if (sent?.LastDeliveryId == deliveryId
                || sent is not null && now - sent.LastSentAt < RepeatInterval)
            {
                await transaction.CommitAsync(cancellationToken);
                return true;
            }

            var response = BuildResponse(route.Address, senderMailbox, original, vacation, now);
            var format = FormatOptions.Default.Clone();
            format.NewLineFormat = NewLineFormat.Dos;
            await using var stream = new MemoryStream();
            await response.WriteToAsync(format, stream, cancellationToken);
            var responseBytes = stream.ToArray();
            if (responseBytes.Length > environment.Limits.MaxMessageSizeBytes)
            {
                await transaction.RollbackAsync(cancellationToken);
                return true;
            }
            var recipientIsLocal = await emailService.CanReceiveAsync(
                senderMailbox.Address,
                cancellationToken);

            if (sent is null)
            {
                sent = new JmapVacationReplyDB
                {
                    Id = Guid.CreateVersion7(),
                    AccountId = route.AccountId,
                    SenderAddress = senderMailbox.Address.ToLowerInvariant(),
                };
                database.JmapVacationReplies.Add(sent);
            }
            sent.LastDeliveryId = deliveryId;
            sent.LastSentAt = now;
            _ = await queue.EnqueueAsync(new MailSubmission(
                Guid.CreateVersion7(),
                string.Empty,
                [new MailEnvelopeRecipient(senderMailbox.Address, recipientIsLocal)],
                Encoding.Latin1.GetString(responseBytes),
                null,
                environment.Smtp.Hostname,
                null), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
    }

    private async Task<VacationRoute?> ResolveVacationRouteAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var separator = address.LastIndexOf('@');
        if (separator <= 0 || separator == address.Length - 1)
            return null;
        var localPart = address[..separator].ToLowerInvariant();
        var domain = address[(separator + 1)..].ToLowerInvariant();
        var route = await database.Inboxes
            .AsNoTracking()
            .Where(inbox => (inbox.Name == localPart || inbox.Name == "*")
                && inbox.Address.Domain == domain
                && inbox.Address.IsActive
                && inbox.Address.Company.IsActive
                && (inbox.Name != "*" || inbox.AliasForInboxId != null))
            .OrderBy(inbox => inbox.Name == localPart ? 0 : 1)
            .Select(inbox => new { inbox.Id, inbox.AliasForInboxId })
            .FirstOrDefaultAsync(cancellationToken);
        if (route is null)
            return null;
        var targetId = route.AliasForInboxId ?? route.Id;
        return await database.Inboxes
            .AsNoTracking()
            .Where(inbox => inbox.Id == targetId
                && inbox.AliasForInboxId == null
                && inbox.Name != "*"
                && inbox.Owner.IsActive)
            .Select(inbox => new VacationRoute(
                inbox.Id,
                inbox.Name + "@" + inbox.Address.Domain))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static bool MustSuppress(MimeMessage message)
    {
        if (message.Headers
            .Where(header => header.Field.Equals(
                "Auto-Submitted",
                StringComparison.OrdinalIgnoreCase))
            .Any(header => !IsManualSubmission(header.Value)))
        {
            return true;
        }
        var precedence = message.Headers["Precedence"]?.Trim();
        if (precedence is not null
            && (precedence.Equals("bulk", StringComparison.OrdinalIgnoreCase)
                || precedence.Equals("list", StringComparison.OrdinalIgnoreCase)
                || precedence.Equals("junk", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (message.Headers.Contains(HeaderId.ListId)
            || message.Headers.Contains(HeaderId.ListPost)
            || message.Headers.Contains("Mailing-List"))
            return true;
        var suppress = message.Headers["X-Auto-Response-Suppress"];
        return suppress?.Split(',').Any(value =>
        {
            var token = value.Trim();
            return token.Equals("all", StringComparison.OrdinalIgnoreCase)
                || token.Equals("oof", StringComparison.OrdinalIgnoreCase)
                || token.Equals("autoreply", StringComparison.OrdinalIgnoreCase);
        }) == true;
    }

    private static bool IsManualSubmission(string value)
    {
        var index = 0;
        if (!TrySkipCommentsAndWhitespace(value, ref index))
            return false;

        var keywordStart = index;
        while (index < value.Length && IsMimeTokenCharacter(value[index]))
            index++;
        if (!value.AsSpan(keywordStart, index - keywordStart)
            .Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        while (true)
        {
            if (!TrySkipCommentsAndWhitespace(value, ref index))
                return false;
            if (index == value.Length)
                return true;
            if (value[index++] != ';'
                || !TrySkipCommentsAndWhitespace(value, ref index)
                || !TryReadMimeToken(value, ref index)
                || !TrySkipCommentsAndWhitespace(value, ref index)
                || index == value.Length
                || value[index++] != '='
                || !TrySkipCommentsAndWhitespace(value, ref index)
                || !TryReadParameterValue(value, ref index))
            {
                return false;
            }
        }
    }

    private static bool TryReadParameterValue(string value, ref int index)
    {
        if (index == value.Length)
            return false;
        if (value[index] != '"')
            return TryReadMimeToken(value, ref index);

        index++;
        while (index < value.Length)
        {
            var character = value[index++];
            if (character == '"')
                return true;
            if (character == '\\')
            {
                if (index == value.Length)
                    return false;
                index++;
            }
            else if (character is '\r' or '\n' or '\0')
            {
                return false;
            }
        }
        return false;
    }

    private static bool TryReadMimeToken(string value, ref int index)
    {
        var start = index;
        while (index < value.Length && IsMimeTokenCharacter(value[index]))
            index++;
        return index > start;
    }

    private static bool IsMimeTokenCharacter(char character) =>
        character is >= (char)33 and <= (char)126
        && character is not ('(' or ')' or '<' or '>' or '@' or ',' or ';'
            or ':' or '\\' or '"' or '/' or '[' or ']' or '?' or '=');

    private static bool TrySkipCommentsAndWhitespace(string value, ref int index)
    {
        while (index < value.Length)
        {
            if (value[index] is ' ' or '\t' or '\r' or '\n')
            {
                index++;
                continue;
            }
            if (value[index] != '(')
                return true;

            var depth = 1;
            index++;
            while (index < value.Length && depth > 0)
            {
                var character = value[index++];
                if (character == '\\')
                {
                    if (index == value.Length)
                        return false;
                    index++;
                }
                else if (character == '(')
                {
                    depth++;
                }
                else if (character == ')')
                {
                    depth--;
                }
                else if (character == '\0')
                {
                    return false;
                }
            }
            if (depth != 0)
                return false;
        }
        return true;
    }

    private static bool IsAutomatedAddress(string address)
    {
        var localPart = address.Split('@', 2)[0];
        return localPart.Equals("mailer-daemon", StringComparison.OrdinalIgnoreCase)
            || localPart.Equals("postmaster", StringComparison.OrdinalIgnoreCase)
            || localPart.Equals("listserv", StringComparison.OrdinalIgnoreCase)
            || localPart.EndsWith("-request", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NamesRecipient(MimeMessage message, params string[] addresses)
    {
        var recognizedAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in addresses)
        {
            if (MailboxAddress.TryParse(address, out var mailbox))
                recognizedAddresses.Add(mailbox.Address);
        }

        foreach (var header in message.Headers)
        {
            if (!RecipientHeaderFields.Contains(header.Field, StringComparer.OrdinalIgnoreCase)
                || !InternetAddressList.TryParse(header.Value, out var recipients))
            {
                continue;
            }

            if (recipients.Mailboxes.Any(mailbox => recognizedAddresses.Contains(mailbox.Address)))
                return true;
        }

        return false;
    }

    private MimeMessage BuildResponse(
        string fromAddress,
        MailboxAddress recipient,
        MimeMessage original,
        JmapVacationResponseDB settings,
        DateTime now)
    {
        var response = new MimeMessage
        {
            Date = new DateTimeOffset(now, TimeSpan.Zero),
            MessageId = MimeUtils.GenerateMessageId(environment.Smtp.Hostname),
            Subject = settings.Subject
                ?? (string.IsNullOrWhiteSpace(original.Subject)
                    ? "Auto: Automatic reply"
                    : $"Auto: {original.Subject}"),
        };
        response.From.Add(MailboxAddress.Parse(fromAddress));
        response.To.Add(recipient);
        response.Headers[HeaderId.AutoSubmitted] = "auto-replied";
        response.Headers["X-Auto-Response-Suppress"] = "All";
        if (!string.IsNullOrWhiteSpace(original.MessageId))
        {
            response.InReplyTo = original.MessageId;
            foreach (var reference in original.References)
                response.References.Add(reference);
            if (response.References.Count == 0
                && !string.IsNullOrWhiteSpace(original.InReplyTo))
            {
                response.References.Add(original.InReplyTo);
            }
            response.References.Add(original.MessageId);
        }
        var body = new BodyBuilder
        {
            TextBody = settings.TextBody,
            HtmlBody = settings.HtmlBody,
        };
        if (body.TextBody is null && body.HtmlBody is null)
            body.TextBody = "I am currently away and may not be able to read your message promptly.";
        response.Body = body.ToMessageBody();
        return response;
    }

    private sealed record VacationRoute(Guid AccountId, string Address);
}
