using System.Text;
using System.Xml;
using System.Xml.Linq;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsAttachmentResponse
{
    internal static async Task<string> ExecuteAsync(GatewayEwsClient application, ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, GatewayEwsRequest request, CancellationToken cancellationToken)
    {
        var ids = request.Attachments ?? throw new InvalidOperationException("The attachment request is incomplete.");
        var plans = ids.Select(id => Resolve(id, account)).ToArray();
        var parents = plans.Where(plan => plan.Code is null).Select(plan => plan.Parent).Distinct().ToArray();
        var read = parents.Length == 0 ? null : await application.ReadItemContentAsync(authentication, profile, account, parents,
            includeText: false, cancellationToken).ConfigureAwait(false);
        var sources = read?.Messages.ToDictionary(item => item.MessageId) ?? [];
        var rendered = new Dictionary<string, (string? Code, XElement? File)>(StringComparer.Ordinal);
        var responses = new XElement(GatewayEwsSoap.Messages + "ResponseMessages");
        long responseBytes = 1024;
        for (var index = 0; index < plans.Length; index++)
        {
            if (!rendered.TryGetValue(ids[index], out var result))
            {
                var plan = plans[index];
                var code = plan.Code;
                XElement? file = null;
                if (code is null)
                {
                    sources.TryGetValue(plan.Parent, out var source);
                    code = source?.Status switch
                    {
                        MailMessageContentStatus.TooLarge => "ErrorDataSizeLimitExceeded",
                        MailMessageContentStatus.NotParsable => "ErrorInvalidPropertyRequest",
                        MailMessageContentStatus.Ok => null,
                        _ => read?.Status == MailMessageReadStatus.RequestTooLarge ? "ErrorDataSizeLimitExceeded" : "ErrorInvalidAttachmentId",
                    };
                    if (code is null)
                    {
                        try
                        {
                            using var catalog = GatewayEwsAttachmentCatalog.Load(source!.Content);
                            file = catalog.Get(account, plan.Parent, plan.Hash, plan.Position);
                        }
                        catch (Exception exception) when (exception is XmlException or FormatException) { code = "ErrorInvalidPropertyRequest"; }
                        catch (GatewayEwsRequestException exception) { code = exception.Code; }
                    }
                }
                result = (code, file);
                rendered.Add(ids[index], result);
            }
            var response = new XElement(GatewayEwsSoap.Messages + "GetAttachmentResponseMessage", new XAttribute("ResponseClass", result.Code is null ? "Success" : "Error"));
            if (result.Code is not null) response.Add(new XElement(GatewayEwsSoap.Messages + "MessageText", "The attachment is unavailable or cannot be represented."));
            response.Add(new XElement(GatewayEwsSoap.Messages + "ResponseCode", result.Code ?? "NoError"));
            if (result.Code is not null) response.Add(new XElement(GatewayEwsSoap.Messages + "DescriptiveLinkKey", 0));
            response.Add(new XElement(GatewayEwsSoap.Messages + "Attachments", result.File is null ? null : new XElement(result.File)));
            responseBytes += Encoding.UTF8.GetByteCount(GatewayEwsSoap.Envelope(new XElement(response)));
            if (GatewayHttpPayloadBudget.BinaryEnvelopeBytes(responseBytes) > application.MaximumPayloadBytes)
                throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded");
            responses.Add(response);
        }
        return GatewayEwsSoap.Envelope(new XElement(GatewayEwsSoap.Messages + "GetAttachmentResponse", responses));
    }

    private static (Guid Parent, string Hash, int Position, string? Code) Resolve(string id, Guid account)
    {
        if (!GatewayEwsAttachmentIdCodec.TryDecode(id, out var owner, out var parent, out var hash, out var position))
            return (Guid.Empty, "", -1, "ErrorInvalidAttachmentId");
        return (parent, hash, position, account == Guid.Empty ? "ErrorInvalidAttachmentId" : owner != account ? "ErrorAccessDenied" : null);
    }
}
