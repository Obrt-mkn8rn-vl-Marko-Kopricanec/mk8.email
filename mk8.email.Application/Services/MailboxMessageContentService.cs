using System.Security.Cryptography;
using System.Text;
using MimeKit;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class MailboxMessageContentService(
    ILargeObjectStore objects,
    LargeObjectTransactionEffects transactionEffects)
{
    public const int MaximumSearchProjectionCharacters = 65_536;

    public async Task SetAsync(
        EmailDB email,
        byte[] rawMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(rawMessage);
        EnsureAzureBlobProvider();
        if (email.Id == Guid.Empty)
            throw new InvalidOperationException("The mailbox message identifier is not valid.");

        var hash = Convert.ToHexStringLower(SHA256.HashData(rawMessage));
        var previous = TryGetReference(email);
        var source = new MemoryStream(rawMessage, writable: false);
        await using (source.ConfigureAwait(false))
        {
            var written = await objects.PutIfAbsentAsync(
            BuildObjectName(email.Id, hash),
            source,
            rawMessage.LongLength,
            hash,
            "message/rfc822",
            cancellationToken).ConfigureAwait(false);
            ApplyReference(email, written.Reference);
            ApplySearchProjection(email, rawMessage);
            if (written.Created)
                transactionEffects.DeleteOnRollback(written.Reference);
            if (previous is not null && previous != written.Reference)
                transactionEffects.DeleteOnCommit(previous);
        }
    }

    public async Task<byte[]> ReadAsync(
        EmailDB email,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        if (email.RawMessage is not null)
        {
            var legacy = email.RawMessage.ToArray();
            if (email.SizeBytes > 0 && legacy.LongLength != email.SizeBytes)
            {
                throw new InvalidOperationException(
                    $"Mailbox message {email.Id:D} failed its legacy length check.");
            }
            return legacy;
        }

        var reference = TryGetReference(email);
        if (reference is null)
            return BuildLegacyRawMessage(email);

        EnsureAzureBlobProvider();
        var destination = new MemoryStream();
        await using (destination.ConfigureAwait(false))
        {
            await objects.CopyToAsync(reference, destination, cancellationToken).ConfigureAwait(false);
            var content = destination.ToArray();
            if (content.LongLength != email.SizeBytes)
            {
                throw new InvalidOperationException(
                    $"Mailbox message {email.Id:D} failed its length check.");
            }
            var hash = SHA256.HashData(content);
            if (!CryptographicOperations.FixedTimeEquals(
                    hash,
                    Convert.FromHexString(reference.Sha256)))
            {
                throw new InvalidOperationException(
                    $"Mailbox message {email.Id:D} failed its content hash check.");
            }
            return content;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "The shipped mailbox content-service API is instance-bound for existing consumers.")]
    public LargeObjectReference? TryGetReference(EmailDB email)
    {
        ArgumentNullException.ThrowIfNull(email);
        if (email.RawMessageObjectProvider is null
            || email.RawMessageObjectName is null
            || email.RawMessageObjectSha256 is null
            || email.RawMessageObjectEntityTag is null)
        {
            return null;
        }
        if (!string.Equals(
                email.RawMessageObjectProvider,
                LargeObjectProviders.AzureBlob,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Mailbox message {email.Id:D} does not use Azure Blob-compatible storage.");
        }
        return new LargeObjectReference(
            email.RawMessageObjectProvider,
            email.RawMessageObjectName,
            email.SizeBytes,
            email.RawMessageObjectSha256,
            email.RawMessageObjectEntityTag);
    }

    public void DeleteOnCommit(EmailDB email)
    {
        var reference = TryGetReference(email);
        if (reference is not null)
            transactionEffects.DeleteOnCommit(reference);
    }

    public static string BuildObjectName(Guid emailId, string sha256) =>
        $"mail/messages/{emailId:N}/{sha256}.eml";

    public static void ApplyReference(EmailDB email, LargeObjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Mailbox message content requires the Azure Blob storage provider.");
        }
        email.RawMessage = null;
        email.SizeBytes = checked((int)reference.Length);
        email.RawMessageObjectProvider = reference.Provider;
        email.RawMessageObjectName = reference.ObjectName;
        email.RawMessageObjectSha256 = reference.Sha256;
        email.RawMessageObjectEntityTag = reference.EntityTag;
    }

    public static void ApplySearchProjection(EmailDB email, byte[] rawMessage)
    {
        ArgumentNullException.ThrowIfNull(email);
        var rawText = MailWireEncoding.Instance.GetString(rawMessage);
        var (headers, fallbackBody) = SplitMessage(rawText);
        email.RawHeaders = Truncate(headers, MaximumSearchProjectionCharacters);

        var projection = new StringBuilder(MaximumSearchProjectionCharacters);
        var parsed = false;
        try
        {
            using var stream = new MemoryStream(rawMessage, writable: false);
            using var message = MimeMessage.Load(stream, persistent: false);
            parsed = true;
            if (message.Body is TextPart { IsAttachment: false })
            {
                AppendProjection(
                    projection,
                    RestoreLineEndings(message.TextBody ?? message.HtmlBody, fallbackBody));
            }
            else
            {
                AppendProjection(projection, message.TextBody);
                if (!string.Equals(message.HtmlBody, message.TextBody, StringComparison.Ordinal))
                    AppendProjection(projection, message.HtmlBody);
            }
        }
        catch (FormatException)
        {
            AppendProjection(projection, fallbackBody);
        }

        email.Body = parsed
            ? projection.ToString()
            : Truncate(fallbackBody, MaximumSearchProjectionCharacters);
    }

    public static byte[] BuildLegacyRawMessage(EmailDB email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var value = email.RawHeaders is null
            ? BuildFallbackMessage(email)
            : email.RawHeaders + "\r\n\r\n" + email.Body;
        return MailWireEncoding.Instance.GetBytes(value);
    }

    private static (string Headers, string Body) SplitMessage(string raw)
    {
        var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator >= 0)
            return (raw[..separator], raw[(separator + 4)..]);
        separator = raw.IndexOf("\n\n", StringComparison.Ordinal);
        return separator >= 0
            ? (raw[..separator], raw[(separator + 2)..])
            : (raw, string.Empty);
    }

    private static void AppendProjection(StringBuilder destination, string? value)
    {
        if (string.IsNullOrEmpty(value) || destination.Length >= MaximumSearchProjectionCharacters)
            return;
        if (destination.Length > 0)
            destination.Append('\n');
        var remaining = MaximumSearchProjectionCharacters - destination.Length;
        destination.Append(Truncate(value, remaining));
    }

    private static string? RestoreLineEndings(string? value, string rawBody)
    {
        if (value is null || !rawBody.Contains("\r\n", StringComparison.Ordinal))
            return value;
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private static string Truncate(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
            return value;
        var length = maximumCharacters;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length];
    }

    private static string BuildFallbackMessage(EmailDB email)
    {
        var builder = new StringBuilder();
        builder.Append("From: ").Append(email.Sender).Append("\r\n");
        builder.Append("To: ").Append(email.Recipient).Append("\r\n");
        if (!string.IsNullOrEmpty(email.Cc))
            builder.Append("Cc: ").Append(email.Cc).Append("\r\n");
        builder.Append("Subject: ").Append(email.Subject).Append("\r\n");
        builder.Append("Date: ").Append(email.ReceivedAt.ToString("r")).Append("\r\n");
        if (!string.IsNullOrEmpty(email.MessageId))
            builder.Append("Message-ID: ").Append(email.MessageId).Append("\r\n");
        if (!string.IsNullOrEmpty(email.InReplyTo))
            builder.Append("In-Reply-To: ").Append(email.InReplyTo).Append("\r\n");
        builder.Append("MIME-Version: 1.0\r\n");
        builder.Append("Content-Type: text/plain; charset=UTF-8\r\n\r\n");
        builder.Append(email.Body);
        return builder.ToString();
    }

    private void EnsureAzureBlobProvider()
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Mailbox message content requires an Azure Blob-compatible object store.");
        }
    }
}
