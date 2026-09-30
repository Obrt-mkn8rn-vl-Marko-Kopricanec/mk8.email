using System.Globalization;
using MimeKit;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static class MailSubmissionEnvelopeBuilder
{
    public static (MailSubmissionEnvelope? Envelope, MailSubmissionMutationFailure? Failure) Build(
        MailSubmissionEnvelopeDraft? draft, byte[] raw, string identityAddress, long maximumSize)
    {
        if (draft is null) return Generate(raw, identityAddress);
        if (draft.InvalidShape || !Valid(draft.Sender, true, raw.LongLength, maximumSize))
            return (null, Invalid());
        var invalidAddresses = new List<string>();
        var malformed = false;
        var recipients = new List<MailEnvelopeAddress>();
        foreach (var item in draft.Recipients)
        {
            if (item.Issue == MailEnvelopeAddressIssue.InvalidAddress
                || item.Issue == MailEnvelopeAddressIssue.None && !ValidAddress(item.Address))
                invalidAddresses.Add(item.Address);
            else if (!Valid(item, false, raw.LongLength, maximumSize)) malformed = true;
            else recipients.Add(new(item.Address, item.Parameters));
        }
        if (malformed) return (null, Invalid());
        if (invalidAddresses.Count > 0)
            return (null, new(MailSubmissionMutationError.InvalidRecipients, null, null, invalidAddresses.ToArray(), null, null));
        return (new(new(draft.Sender.Address, draft.Sender.Parameters), recipients.ToArray()), null);
    }

    private static (MailSubmissionEnvelope? Envelope, MailSubmissionMutationFailure? Failure) Generate(byte[] raw, string identity)
    {
        try
        {
            using var message = JmapEmailCodec.Parse(raw);
            var sender = message.Sender?.Address ?? message.From.Mailboxes.FirstOrDefault()?.Address ?? identity;
            if (!string.Equals(sender, identity, StringComparison.OrdinalIgnoreCase)) sender = identity;
            var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes)
                .Select(address => address.Address).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var invalid = recipients.Where(address => !ValidAddress(address)).ToArray();
            return invalid.Length > 0
                ? (null, new(MailSubmissionMutationError.InvalidRecipients, null, null, invalid, null, null))
                : (new(new(sender, null), recipients.Select(address => new MailEnvelopeAddress(address, null)).ToArray()), null);
        }
        catch (FormatException)
        {
            return (null, new(MailSubmissionMutationError.InvalidEmail, null, null, null, null, null));
        }
    }

    private static bool Valid(MailEnvelopeAddressDraft item, bool sender, long actual, long maximum)
    {
        if (item.Issue != MailEnvelopeAddressIssue.None || !ValidAddress(item.Address)) return false;
        if (item.Parameters is null) return true;
        if (!sender) return item.Parameters.Count == 0;
        return item.Parameters.Count == 0 || item.Parameters.Count == 1
            && item.Parameters.TryGetValue("SIZE", out var text) && text.Length > 0 && text.All(char.IsAsciiDigit)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size >= actual && size <= maximum;
    }

    private static bool ValidAddress(string email)
    {
        var separator = email.LastIndexOf('@');
        return email.Length <= 254 && email.All(char.IsAscii) && separator is > 0 and <= 64
            && email.Length - separator - 1 is > 0 and <= 255 && MailboxAddress.TryParse(email, out var mailbox)
            && string.Equals(mailbox.Address, email, StringComparison.OrdinalIgnoreCase);
    }

    private static MailSubmissionMutationFailure Invalid() =>
        new(MailSubmissionMutationError.InvalidProperties, null, ["envelope"], null, null, null);
}
