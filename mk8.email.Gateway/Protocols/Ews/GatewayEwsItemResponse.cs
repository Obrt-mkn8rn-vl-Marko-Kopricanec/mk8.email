using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemResponse
{
    private static readonly GatewayEmailProjectionOptions Projection = new(
        ["subject", "sentAt", "hasAttachment", "messageId", "sender", "from", "to", "cc", "bcc", "replyTo"],
        [], false, false, false, 0);

    internal static async Task<string> ExecuteAsync(GatewayEwsClient application, ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, GatewayEwsRequest request, CancellationToken cancellationToken)
    {
        var references = request.Items ?? throw new InvalidOperationException("The EWS item request is incomplete.");
        var plans = references.Select(reference => Resolve(reference, account)).ToArray();
        var ids = plans.Where(plan => plan.Code is null).Select(plan => plan.Id).Distinct().ToArray();
        var read = ids.Length == 0 ? null : await application.ReadItemsAsync(authentication, profile, account, ids, cancellationToken,
            includeText: request.Properties.Contains("Body")).ConfigureAwait(false);
        return Render(request, plans, account, read, application.MaximumPayloadBytes);
    }

    private static (Guid Id, string? Code) Resolve(GatewayEwsItemReference reference, Guid account)
    {
        if (!GatewayEwsItemIdCodec.TryDecode(reference.Id, out var requestedAccount, out var id))
            return (Guid.Empty, "ErrorInvalidIdMalformed");
        if (account == Guid.Empty) return (Guid.Empty, "ErrorItemNotFound");
        if (requestedAccount != account) return (Guid.Empty, "ErrorAccessDenied");
        if (reference.ChangeKey is not null)
        {
            Span<byte> key = stackalloc byte[1024];
            if (!Convert.TryFromBase64String(reference.ChangeKey, key, out var written) || written == 0
                || !string.Equals(reference.ChangeKey, Convert.ToBase64String(key[..written]), StringComparison.Ordinal))
                return (Guid.Empty, "ErrorInvalidChangeKey");
        }
        // Reads return the current conservative version, not a conditional mutation.
        return (id, null);
    }

    private static string Render(GatewayEwsRequest request, (Guid Id, string? Code)[] plans,
        Guid account, MailMessageReadResult? read, int maximumPayloadBytes)
    {
        var byId = read?.Messages.ToDictionary(item => item.MessageId, item => item.Value) ?? [];
        var failure = read?.Status == MailMessageReadStatus.RequestTooLarge ? "ErrorDataSizeLimitExceeded" : "ErrorItemNotFound";
        var responses = new XElement(GatewayEwsSoap.Messages + "ResponseMessages");
        // Conservatively count each row in its own prefix-qualified SOAP envelope.
        // The fixed reserve covers the actual GetItem/ResponseMessages wrappers;
        // duplicated per-row envelope overhead prevents undercounting namespaces.
        long responseBytes = 1024;
        foreach (var plan in plans)
        {
            var code = plan.Code;
            XElement? item = null;
            byId.TryGetValue(plan.Id, out var snapshot);
            if (code is null && snapshot is null) code = failure;
            else if (code is null)
            {
                try { item = Message(account, read!.State!, snapshot!, request.Properties, request.BodyType); }
                catch (XmlException) { code = "ErrorInvalidPropertyRequest"; }
                catch (GatewayEwsRequestException exception) { code = exception.Code; }
            }
            var response = new XElement(GatewayEwsSoap.Messages + "GetItemResponseMessage", new XAttribute("ResponseClass", code is null ? "Success" : "Error"));
            if (code is not null) response.Add(new XElement(GatewayEwsSoap.Messages + "MessageText", "The mail item is unavailable or cannot be represented."));
            response.Add(new XElement(GatewayEwsSoap.Messages + "ResponseCode", code ?? "NoError"));
            if (code is not null) response.Add(new XElement(GatewayEwsSoap.Messages + "DescriptiveLinkKey", 0));
            response.Add(new XElement(GatewayEwsSoap.Messages + "Items", item));
            if (request.Properties.Contains("Body"))
            {
                responseBytes += Encoding.UTF8.GetByteCount(GatewayEwsSoap.Envelope(new XElement(response)));
                if (GatewayHttpPayloadBudget.BinaryEnvelopeBytes(responseBytes) > maximumPayloadBytes)
                    throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded");
            }
            responses.Add(response);
        }
        return GatewayEwsSoap.Envelope(new XElement(GatewayEwsSoap.Messages + "GetItemResponse", responses));
    }

    internal static XElement Message(Guid account, string state, MailMessageSnapshot snapshot, IReadOnlySet<string> properties, string bodyType = "Best")
    {
        var value = GatewayEmailValueCodec.BuildEmail(snapshot, Projection);
        var stored = snapshot.Stored ?? throw new InvalidOperationException("The EWS item has no stored identity.");
        var result = new XElement(GatewayEwsSoap.Types + "Message", new XElement(GatewayEwsSoap.Types + "ItemId",
            new XAttribute("Id", GatewayEwsItemIdCodec.Encode(account, stored.Id)),
            new XAttribute("ChangeKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(state)))));
        if (properties.Contains("ParentFolderId")) result.Add(new XElement(GatewayEwsSoap.Types + "ParentFolderId",
            new XAttribute("Id", GatewayEwsFolderIdCodec.Encode(account, stored.FolderId))));
        Add(result, properties, "ItemClass", "IPM.Note");
        Add(result, properties, "Subject", value["subject"]?.GetValue<string>() ?? "");
        if (properties.Contains("Body")) result.Add(GatewayEwsBodyCodec.Render(snapshot, bodyType));
        Add(result, properties, "DateTimeReceived", stored.ReceivedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        Add(result, properties, "Size", stored.Size);
        Add(result, properties, "IsDraft", stored.Keywords.Contains("$draft", StringComparer.Ordinal));
        Add(result, properties, "DateTimeSent", value["sentAt"]?.GetValue<string>());
        Add(result, properties, "HasAttachments", value["hasAttachment"]!.GetValue<bool>());
        AddRecipients(result, properties, "Sender", value["sender"] ?? value["from"], single: true);
        AddRecipients(result, properties, "ToRecipients", value["to"], single: false);
        AddRecipients(result, properties, "CcRecipients", value["cc"], single: false);
        AddRecipients(result, properties, "BccRecipients", value["bcc"], single: false);
        AddRecipients(result, properties, "From", value["from"], single: true);
        var internetId = (value["messageId"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
        Add(result, properties, "InternetMessageId", internetId is null ? null : $"<{internetId}>");
        Add(result, properties, "IsRead", stored.Keywords.Contains("$seen", StringComparer.Ordinal));
        AddRecipients(result, properties, "ReplyTo", value["replyTo"], single: false);
        return result;
    }

    private static void Add(XElement message, IReadOnlySet<string> properties, string name, object? value)
    {
        if (!properties.Contains(name) || value is null) return;
        if (value is string text)
        {
            if (name is "Subject" && text.Length > 255) throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded");
            XmlConvert.VerifyXmlChars(text);
        }
        message.Add(new XElement(GatewayEwsSoap.Types + name, value));
    }

    private static void AddRecipients(XElement message, IReadOnlySet<string> properties, string name, JsonNode? values, bool single)
    {
        if (!properties.Contains(name) || values is not JsonArray addresses || addresses.Count == 0) return;
        // EWS From/Sender contain one Mailbox. Do not invent a single sender for a multi-author header.
        if (single && addresses.Count != 1) throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        var recipients = new XElement(GatewayEwsSoap.Types + name);
        foreach (var address in addresses)
        {
            var email = address!["email"]!.GetValue<string>();
            var displayName = address["name"]?.GetValue<string>();
            XmlConvert.VerifyXmlChars(email);
            if (displayName is not null) XmlConvert.VerifyXmlChars(displayName);
            recipients.Add(new XElement(GatewayEwsSoap.Types + "Mailbox",
                displayName is null ? null : new XElement(GatewayEwsSoap.Types + "Name", displayName),
                new XElement(GatewayEwsSoap.Types + "EmailAddress", email), new XElement(GatewayEwsSoap.Types + "RoutingType", "SMTP")));
        }
        message.Add(recipients);
    }
}
