using MimeKit;
using MimeKit.Utils;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class MailMimeDraftBuilder(JmapBlobService blobs)
{
    public async Task<(byte[]? Raw, MailMessageMutationFailure? Failure)> BuildAsync(
        Guid accountId, string accountAddress, JmapInvocationContext context,
        MailMessageDraft value, CancellationToken cancellationToken)
    {
        if (value.Failure is not null && !value.CheckBlobsBeforeFailure)
            return (null, value.Failure);
        var (contents, failure) = await ReadContentsAsync(accountId, context, value.BlobReferences,
            cancellationToken).ConfigureAwait(false);
        if (failure is not null) return (null, failure);
        if (value.Failure is not null) return (null, value.Failure);
        var mime = value.Mime ?? throw new InvalidOperationException("The MIME draft is missing.");
        using var message = new MimeMessage((IEnumerable<Header>)Array.Empty<Header>());
        try
        {
            ReplaceHeaders(message.Headers, mime.Headers);
            AddDefaults(message, accountAddress);
            var row = mime.Parts[mime.RootPart];
            message.Body = CreateEntity(row);
            Populate(message.Body, mime, mime.RootPart, contents!);
            using var stream = new MemoryStream();
            var format = FormatOptions.Default.Clone();
            format.NewLineFormat = NewLineFormat.Dos;
            await message.WriteToAsync(format, stream, cancellationToken).ConfigureAwait(false);
            return (stream.ToArray(), null);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return (null, new(MailMessageMutationError.InvalidEmail, exception.Message, null, null));
        }
    }

    private async Task<(Dictionary<string, byte[]>? Contents, MailMessageMutationFailure? Failure)> ReadContentsAsync(
        Guid accountId, JmapInvocationContext context, IReadOnlyList<string> references,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.AreValidIdReferences(references, context)
            || references.Any(reference => context.ResolveId(reference) is null))
            return (null, new(MailMessageMutationError.InvalidProperties, null, ["blobId"], null));
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var reference in references)
        {
            var resolved = context.ResolveId(reference)!;
            var blob = await blobs.GetAsync(accountId, resolved, cancellationToken).ConfigureAwait(false);
            if (blob is null) missing.Add(resolved);
            else contents[reference] = blob.Content;
        }
        return missing.Count > 0
            ? (null, new(MailMessageMutationError.BlobNotFound, null, null, missing.ToArray()))
            : (contents, null);
    }

    private static void AddDefaults(MimeMessage message, string accountAddress)
    {
        if (!message.Headers.Contains(HeaderId.From))
            message.From.Add(MailboxAddress.Parse(accountAddress));
        if (!message.Headers.Contains(HeaderId.MessageId))
            message.MessageId = MimeUtils.GenerateMessageId(accountAddress[(accountAddress.LastIndexOf('@') + 1)..]);
        if (!message.Headers.Contains(HeaderId.Date)) message.Date = DateTimeOffset.UtcNow;
    }

    private static MimeEntity CreateEntity(MailMimeDraftPart row)
    {
        if (!ContentType.TryParse(row.MediaType, out var type))
            throw new FormatException("The MIME draft media type is invalid.");
        if (string.Equals(type.MediaType, "multipart", StringComparison.OrdinalIgnoreCase))
            return new Multipart(type.MediaSubtype);
        return row.Text is not null ? new TextPart(type.MediaSubtype) { Text = row.Text }
            : new MimePart(type.MediaType, type.MediaSubtype);
    }

    private static void Populate(MimeEntity entity, MailMimeDraft mime, int index,
        Dictionary<string, byte[]> contents)
    {
        var row = mime.Parts[index];
        ReplaceHeaders(entity.Headers, row.Headers);
        if (entity is Multipart multipart)
        {
            foreach (var childIndex in row.Children)
            {
                var child = CreateEntity(mime.Parts[childIndex]);
                multipart.Add(child);
                Populate(child, mime, childIndex, contents);
            }
        }
        else if (row.BlobReference is { } reference)
        {
            if (!contents.TryGetValue(reference, out var content))
                throw new InvalidOperationException("The MIME source was not authorized.");
            ((MimePart)entity).Content = new MimeContent(new MemoryStream(content, writable: false), ContentEncoding.Default);
        }
    }

    private static void ReplaceHeaders(HeaderList headers, IReadOnlyList<MailMimeHeaderSnapshot> values)
    {
        headers.Clear();
        foreach (var value in values)
        {
            var bytes = new byte[value.RawField.Length + value.RawValue.Length + 1];
            value.RawField.Span.CopyTo(bytes);
            bytes[value.RawField.Length] = (byte)':';
            value.RawValue.Span.CopyTo(bytes.AsSpan(value.RawField.Length + 1));
            if (!Header.TryParse(bytes, out var header)) throw new FormatException("A MIME draft header is invalid.");
            headers.Add(header);
        }
    }
}
