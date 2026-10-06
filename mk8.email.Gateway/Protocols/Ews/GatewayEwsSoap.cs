using System.Text;
using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsSoap
{
    internal static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    internal static readonly XNamespace Messages = "http://schemas.microsoft.com/exchange/services/2006/messages";
    internal static readonly XNamespace Types = "http://schemas.microsoft.com/exchange/services/2006/types";
    private static readonly XNamespace Errors = "http://schemas.microsoft.com/exchange/services/2006/errors";

    public static string Envelope(XElement body) => "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", Soap),
            new XAttribute(XNamespace.Xmlns + "m", Messages), new XAttribute(XNamespace.Xmlns + "t", Types),
            new XElement(Soap + "Body", body)).ToString(SaveOptions.DisableFormatting);

    public static string Fault(string code, string message, string prefix = "t", bool isHeaderFault = false)
    {
        var fault = new XElement(Soap + "Fault", new XElement("faultcode", $"{prefix}:{code}"),
            new XElement("faultstring", message));
        // SOAP header/version faults must not pretend that the Body was processed.
        if (!isHeaderFault && string.Equals(prefix, "t", StringComparison.Ordinal))
            fault.Add(new XElement("detail", new XElement(Errors + "ResponseCode", code),
                new XElement(Errors + "Message", message)));
        return Envelope(fault);
    }

    public static async Task WriteAsync(HttpContext context, Stream output, string xml,
        int status = StatusCodes.Status200OK)
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
