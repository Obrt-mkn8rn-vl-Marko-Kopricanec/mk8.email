using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MimeKit;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal sealed class GatewayEwsAttachmentCatalog : IDisposable
{
    private readonly MimeMessage _message;
    private readonly List<(MimePart Part, bool Inline)> _files = [];
    private readonly byte[] _hash;

    private GatewayEwsAttachmentCatalog(MimeMessage message, byte[] hash)
    {
        _message = message;
        _hash = hash;
        Visit(message.Body, related: false, depth: 0);
    }

    internal static GatewayEwsAttachmentCatalog Load(ReadOnlyMemory<byte> raw)
    {
        if (raw.IsEmpty || raw.Length > GatewayEwsClient.MaximumMimeBytes) throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded");
        using var source = new MemoryStream(raw.ToArray(), writable: false);
        MimeMessage? message = null;
        try
        {
            message = MimeMessage.Load(source, persistent: false);
            return new(message, SHA256.HashData(raw.Span));
        }
        catch
        {
            message?.Dispose();
            throw;
        }
    }

    private void Visit(MimeEntity? entity, bool related, int depth)
    {
        if (entity is null) return;
        if (depth > 64) throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded");
        if (entity is Multipart multipart)
        {
            // A multipart disposition applies to the whole subtree. Do not
            // fabricate child file attachments from a grouped attachment.
            if (entity.IsAttachment || entity.ContentDisposition is not null && !IsInline(entity.ContentDisposition.Disposition)) Unsupported();
            for (var index = 0; index < multipart.Count; index++)
                Visit(multipart[index], related || multipart is MultipartRelated relatedParts && !ReferenceEquals(multipart[index], relatedParts.Root), depth + 1);
            return;
        }
        var part = entity as MimePart ?? throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest"); // Exchange item attachments need their own profile.
        var disposition = part.ContentDisposition;
        if (disposition is not null && !disposition.IsAttachment && !IsInline(disposition.Disposition)) Unsupported();
        var text = part.ContentType.IsMimeType("text", "plain") || part.ContentType.IsMimeType("text", "html");
        if (!part.IsAttachment && part.FileName is null && text && !related) return;
        if (part.Content is null) Unsupported();
        if (_files.Count == GatewayEwsAttachmentIdCodec.MaximumAttachments) throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded");
        _files.Add((part, IsInline(disposition?.Disposition) || related && !part.IsAttachment));
    }

    internal XElement Metadata(Guid account, Guid parent)
    {
        var attachments = new XElement(GatewayEwsSoap.Types + "Attachments");
        for (var index = 0; index < _files.Count; index++) attachments.Add(Render(account, parent, index, includeContent: false));
        return attachments;
    }

    internal XElement Get(Guid account, Guid parent, string expectedHash, int position)
    {
        if (!string.Equals(expectedHash, Convert.ToHexStringLower(_hash), StringComparison.Ordinal) || position < 0 || position >= _files.Count)
            throw new GatewayEwsRequestException("ErrorInvalidAttachmentId");
        return Render(account, parent, position, includeContent: true);
    }

    private XElement Render(Guid account, Guid parent, int position, bool includeContent)
    {
        var (part, inline) = _files[position];
        using var content = new GatewayCaptureBuffer(GatewayEwsClient.MaximumMimeBytes, StatusCodes.Status413PayloadTooLarge);
        try { (part.Content ?? throw new InvalidOperationException("The attachment has no encoded content.")).DecodeTo(content); }
        catch (BadHttpRequestException) { throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded"); }
        var name = part.FileName ?? "";
        XmlConvert.VerifyXmlChars(name);
        XmlConvert.VerifyXmlChars(part.ContentType.MimeType);
        if (part.ContentId is not null) XmlConvert.VerifyXmlChars(part.ContentId);
        if (part.ContentLocation is not null) XmlConvert.VerifyXmlChars(part.ContentLocation.OriginalString);
        var file = new XElement(GatewayEwsSoap.Types + "FileAttachment",
            new XElement(GatewayEwsSoap.Types + "AttachmentId", new XAttribute("Id", GatewayEwsAttachmentIdCodec.Encode(account, parent, _hash, position))),
            new XElement(GatewayEwsSoap.Types + "Name", name),
            new XElement(GatewayEwsSoap.Types + "ContentType", part.ContentType.MimeType),
            part.ContentId is null ? null : new XElement(GatewayEwsSoap.Types + "ContentId", part.ContentId),
            part.ContentLocation is null ? null : new XElement(GatewayEwsSoap.Types + "ContentLocation", part.ContentLocation.OriginalString),
            new XElement(GatewayEwsSoap.Types + "Size", content.Length),
            new XElement(GatewayEwsSoap.Types + "IsInline", inline));
        if (includeContent) file.Add(new XElement(GatewayEwsSoap.Types + "Content", Convert.ToBase64String(content.GetBuffer(), 0, checked((int)content.Length))));
        return file;
    }

    public void Dispose() => _message.Dispose();

    internal static bool HasNonInlineAttachments(MailMessageSnapshot snapshot)
    {
        if (snapshot.RootPart is null) return false;
        return VisitSnapshot(snapshot, snapshot.RootPart.Value, related: false, depth: 0);
    }

    private static bool VisitSnapshot(MailMessageSnapshot snapshot, int index, bool related, int depth)
    {
        if (depth > 64 || index < 0 || index >= snapshot.Parts.Count) throw new InvalidOperationException("Invalid attachment snapshot graph.");
        var part = snapshot.Parts[index];
        var attached = string.Equals(part.Disposition, "attachment", StringComparison.OrdinalIgnoreCase);
        if (part.MediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            if (attached) return true;
            var rootChild = part.Children.Count == 0 ? -1 : part.Children[0];
            var isRelated = string.Equals(part.MediaType, "multipart/related", StringComparison.OrdinalIgnoreCase);
            if (isRelated)
            {
                var header = part.Headers.FirstOrDefault(value => value.RawField.Span.SequenceEqual("Content-Type"u8)
                    || Encoding.ASCII.GetString(value.RawField.Span).Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
                if (header is not null && ContentType.TryParse(Encoding.ASCII.GetString(header.RawValue.Span), out var type)
                    && type.Parameters["start"] is { } start)
                {
                    rootChild = part.Children.FirstOrDefault(child => child >= 0 && child < index
                        && string.Equals(snapshot.Parts[child].ContentId, start.Trim('<', '>'), StringComparison.Ordinal), -1);
                }
            }
            foreach (var child in part.Children)
            {
                if (child < 0 || child >= index) throw new InvalidOperationException("Invalid attachment snapshot ancestry.");
                if (VisitSnapshot(snapshot, child, related || isRelated && child != rootChild, depth + 1)) return true;
            }
            return false;
        }
        if (IsInline(part.Disposition) || related && !attached) return false;
        var text = part.MediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) || part.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase);
        return attached || part.Name is not null || !text;
    }

    private static bool IsInline(string? disposition) => string.Equals(disposition, "inline", StringComparison.OrdinalIgnoreCase);

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Unsupported() => throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
}
