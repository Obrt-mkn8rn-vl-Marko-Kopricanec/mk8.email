using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using MimeKit.Text;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsBodyCodec
{
    internal const int MaximumBodyBytes = 1_048_576;
    private static readonly GatewayEmailProjectionOptions Projection = new(
        ["textBody", "htmlBody", "bodyValues"], ["partId", "type"], false, true, true, MaximumBodyBytes);
    private static readonly HashSet<string> IgnoredTags = new(["head", "script", "style", "template"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> BlockTags = new(
        ["br", "p", "div", "li", "tr", "pre", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6"], StringComparer.OrdinalIgnoreCase);

    internal static XElement Render(MailMessageSnapshot snapshot, string requestedType)
    {
        if (requestedType is not ("Best" or "Text" or "HTML")) throw new InvalidOperationException("Invalid EWS body format.");
        var visible = GatewayEwsBodyVisibility.Project(snapshot);
        var projection = GatewayEmailValueCodec.BuildEmail(visible, Projection);
        var htmlParts = (JsonArray)projection["htmlBody"]!;
        var html = requestedType is "HTML" || requestedType is "Best" && htmlParts.Any(part => part!["type"]!.GetValue<string>() is "text/html");
        var parts = html ? htmlParts : (JsonArray)projection["textBody"]!;
        var values = (JsonObject)projection["bodyValues"]!;
        if (!parts.Any(part => part!["type"]!.GetValue<string>() is "text/plain" or "text/html")
            && visible.Parts.Any(part => part.Children.Count == 0 && part.Name is null
                && !string.Equals(part.Disposition, "attachment", StringComparison.OrdinalIgnoreCase)
                && !part.MediaType.StartsWith("multipart/", StringComparison.Ordinal)
                && !part.MediaType.StartsWith("image/", StringComparison.Ordinal)
                && !part.MediaType.StartsWith("audio/", StringComparison.Ordinal)
                && !part.MediaType.StartsWith("video/", StringComparison.Ordinal)))
            throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        var sources = visible.Parts.Where(part => part.Path is not null).ToDictionary(part => part.Path!, StringComparer.Ordinal);
        var output = new StringBuilder();
        var usedBytes = 0;
        var sourceBytes = 0;
        foreach (var part in parts)
        {
            var type = part!["type"]!.GetValue<string>();
            // Inline binary media remains a reference in HTML, never an attachment/body disclosure.
            if (type is not ("text/plain" or "text/html")) continue;
            var id = part["partId"]!.GetValue<string>();
            if (!sources.TryGetValue(id, out var source))
                throw new InvalidOperationException("The Application omitted requested body text.");
            var text = ReadText(values[id], source);
            sourceBytes += Encoding.UTF8.GetByteCount(text);
            if (sourceBytes > MaximumBodyBytes) TooLarge();
            if (html && type is "text/plain") text = $"<pre>{WebUtility.HtmlEncode(text)}</pre>";
            else if (!html && type is "text/html") text = PlainText(text);
            var separator = output.Length == 0 ? "" : "\n";
            usedBytes += Encoding.UTF8.GetByteCount(separator) + Encoding.UTF8.GetByteCount(text);
            if (usedBytes > MaximumBodyBytes) TooLarge();
            output.Append(separator).Append(text);
        }
        var body = output.ToString();
        XmlConvert.VerifyXmlChars(body);
        return new XElement(GatewayEwsSoap.Types + "Body", new XAttribute("BodyType", html ? "HTML" : "Text"),
            new XAttribute("IsTruncated", false), body);
    }

    private static string ReadText(JsonNode? value, MailMimePartSnapshot source)
    {
        if (value is null || source.Text is null) throw new InvalidOperationException("The Application omitted requested body text.");
        if (value["isTruncated"]!.GetValue<bool>()) TooLarge();
        if (value["isEncodingProblem"]!.GetValue<bool>()) throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        var text = value["value"]!.GetValue<string>();
        XmlConvert.VerifyXmlChars(text);
        return text;
    }

    private static string PlainText(string html)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        var output = new StringBuilder();
        string? ignored = null;
        var ignoredDepth = 0;
        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                var name = tag.Name;
                if (ignored is not null)
                {
                    if (string.Equals(name, ignored, StringComparison.OrdinalIgnoreCase))
                    {
                        ignoredDepth += tag.IsEndTag ? -1 : 1;
                        if (ignoredDepth == 0) ignored = null;
                    }
                    continue;
                }
                if (!tag.IsEndTag && IgnoredTags.Contains(name)) { ignored = name; ignoredDepth = 1; continue; }
                if (BlockTags.Contains(name))
                {
                    if (output.Length != 0 && output[^1] != '\n') output.Append('\n');
                }
                else if (string.Equals(name, "td", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "th", StringComparison.OrdinalIgnoreCase)) output.Append('\t');
            }
            else if (ignored is null && token.Kind is HtmlTokenKind.Data && token is HtmlDataToken data)
                output.Append(data.Data);
            if (output.Length > MaximumBodyBytes) TooLarge();
        }
        return output.ToString();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void TooLarge() => throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded");
}
