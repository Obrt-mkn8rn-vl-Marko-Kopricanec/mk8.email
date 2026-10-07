using System.Text;
using MimeKit;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsRelatedRoot
{
    // Both raw MIME and neutral snapshots use the pinned library's CID parsing,
    // type selection and first-child fallback. Only headers are projected here;
    // no body is decoded, loaded, or changed to resolve the root.
    internal static int Resolve(IEnumerable<(ReadOnlyMemory<byte> Field, ReadOnlyMemory<byte> Value)> headers,
        IEnumerable<(string MediaType, string? ContentId)> children)
    {
        var values = headers.Where(header => Encoding.ASCII.GetString(header.Field.Span).Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            .Select(header => header.Value).Take(2).ToArray();
        if (values.Length != 1 || !ContentType.TryParse(values[0].ToArray(), out var type)
            || !type.IsMimeType("multipart", "related"))
            throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        using var projection = new MultipartRelated();
        if (type.Parameters["start"] is { } start)
        {
            if (start.Length == 0) throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
            projection.ContentType.Parameters["start"] = start;
        }
        if (type.Parameters["type"] is { } rootType) projection.ContentType.Parameters["type"] = rootType;
        foreach (var child in children)
        {
            var childType = ContentType.Parse(child.MediaType);
            var part = new MimePart(childType.MediaType, childType.MediaSubtype);
            try
            {
                if (child.ContentId is not null) part.ContentId = child.ContentId;
                projection.Add(part);
            }
            catch
            {
                part.Dispose();
                throw;
            }
        }
        try
        {
            var root = projection.Root;
            return root is null ? -1 : projection.IndexOf(root);
        }
        catch (UriFormatException)
        {
            throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        }
    }
}
