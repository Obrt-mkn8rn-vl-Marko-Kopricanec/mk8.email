using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.MailWire;

namespace mk8.email.Gateway.Protocols.Autodiscover;

internal static class GatewayAutodiscoverXml
{
    internal const string RequestNamespace = "http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006";
    internal const string ResponseNamespace = "http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a";
    private static readonly XNamespace EnvelopeNamespace = "http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006";

    public static async Task<GatewayAutodiscoverRequest> ReadAsync(Stream body, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(body, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = GatewayHttpPayloadBudget.SmallRequestBytes,
            IgnoreComments = true,
        });
        try
        {
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            return Parse(document);
        }
        catch (XmlException)
        {
            return new GatewayAutodiscoverRequest(null, 600);
        }
    }

    private static GatewayAutodiscoverRequest Parse(XDocument document)
    {
        XNamespace requestNamespace = RequestNamespace;
        var root = document.Root;
        if (root is null || root.Name != requestNamespace + "Autodiscover" || root.Elements().Count() != 1
            || HasInvalidContainerContent(root))
            return new GatewayAutodiscoverRequest(null, 600);
        var request = root.Elements().First();
        if (request.Name != requestNamespace + "Request"
            || HasInvalidContainerContent(request))
            return new GatewayAutodiscoverRequest(null, 600);
        var fields = request.Elements().ToArray();
        if (fields.Any(field => field.Name.Namespace != requestNamespace || field.HasElements
                || field.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration)
                || field.Name.LocalName is not ("EMailAddress" or "LegacyDN" or "AcceptableResponseSchema"))
            || fields.GroupBy(field => field.Name).Any(group => group.Count() > 1))
            return new GatewayAutodiscoverRequest(null, 600);
        if (!HasRegisteredFieldOrder(fields))
            return new GatewayAutodiscoverRequest(null, 600);
        var schema = request.Element(requestNamespace + "AcceptableResponseSchema")?.Value.Trim();
        if (schema is null)
            return new GatewayAutodiscoverRequest(null, 600);
        if (!string.Equals(schema, ResponseNamespace, StringComparison.Ordinal))
            return new GatewayAutodiscoverRequest(null, 601);
        var address = request.Element(requestNamespace + "EMailAddress")?.Value.Trim();
        if (address is null && request.Element(requestNamespace + "LegacyDN") is not null)
            return new GatewayAutodiscoverRequest(null, 601);
        return SmtpAddress.TryNormalize(address ?? string.Empty, allowEmpty: false, out var normalized)
            ? new GatewayAutodiscoverRequest(normalized, 0)
            : new GatewayAutodiscoverRequest(null, 600);
    }

    private static bool HasInvalidContainerContent(XElement element) =>
        element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration)
        || element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value));

    private static bool HasRegisteredFieldOrder(XElement[] fields)
    {
        var previous = -1;
        foreach (var field in fields)
        {
            var position = field.Name.LocalName switch { "EMailAddress" => 0, "LegacyDN" => 1, _ => 2 };
            if (position <= previous)
                return false;
            previous = position;
        }
        return true;
    }

    public static string Settings(string username, EnvironmentConfig environment)
    {
        XNamespace responseNamespace = ResponseNamespace;
        var account = new XElement(responseNamespace + "Account",
            new XElement(responseNamespace + "AccountType", "email"),
            new XElement(responseNamespace + "Action", "settings"));
        if (environment.Imap.EnableImplicitTls)
            account.Add(Protocol("IMAP", environment.Imap.ImplicitTlsPort, username, environment.Smtp.Hostname));
        if (environment.Pop3.EnableImplicitTls)
            account.Add(Protocol("POP3", environment.Pop3.ImplicitTlsPort, username, environment.Smtp.Hostname));
        if (environment.Smtp.EnableImplicitTls)
            account.Add(Protocol("SMTP", environment.Smtp.ImplicitTlsPort, username, environment.Smtp.Hostname));
        if (!account.Elements(responseNamespace + "Protocol").Any())
            return Error(501, "No supported secure mail configuration is available.");
        return Document(new XElement(responseNamespace + "Response",
            new XElement(responseNamespace + "User", new XElement(responseNamespace + "DisplayName", username)),
            account));
    }

    private static XElement Protocol(string type, int port, string username, string hostname)
    {
        XNamespace responseNamespace = ResponseNamespace;
        return new XElement(responseNamespace + "Protocol",
            new XElement(responseNamespace + "Type", type),
            new XElement(responseNamespace + "Server", hostname),
            new XElement(responseNamespace + "Port", port.ToString(CultureInfo.InvariantCulture)),
            new XElement(responseNamespace + "DomainRequired", "off"),
            new XElement(responseNamespace + "LoginName", username),
            new XElement(responseNamespace + "SPA", "off"),
            new XElement(responseNamespace + "SSL", "on"),
            new XElement(responseNamespace + "AuthRequired", "on"));
    }

    public static string Error(int code, string message) => Document(new XElement(EnvelopeNamespace + "Response",
        new XElement(EnvelopeNamespace + "Error",
            new XAttribute("Time", DateTimeOffset.UtcNow.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
            new XAttribute("Id", "0"),
            new XElement(EnvelopeNamespace + "ErrorCode", code.ToString(CultureInfo.InvariantCulture)),
            new XElement(EnvelopeNamespace + "Message", message),
            new XElement(EnvelopeNamespace + "DebugData"))));

    private static string Document(XElement response) => "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + new XElement(EnvelopeNamespace + "Autodiscover", response).ToString(SaveOptions.DisableFormatting);

    public static async Task WriteAsync(HttpContext context, Stream output, string xml, int status = StatusCodes.Status200OK)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/xml; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
            await output.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}
