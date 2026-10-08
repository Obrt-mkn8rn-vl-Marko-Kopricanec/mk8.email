using System.Text;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemCreateResponse
{
    public static string Render(Guid account, IEnumerable<GatewayEwsItemCreatePlan> plans, MailImportResult? result, string? accountError = null)
    {
        var outcomes = result?.Items.ToDictionary(item => item.CreationId, StringComparer.Ordinal) ?? [];
        var responses = new XElement(GatewayEwsSoap.Messages + "ResponseMessages");
        foreach (var plan in plans)
        {
            var code = plan.Code ?? accountError;
            MailImportItemOutcome? outcome = null;
            if (code is null)
            {
                outcomes.TryGetValue(plan.Token, out outcome);
                code = result?.Status switch
                {
                    MailImportStatus.AccountNotFound => "ErrorFolderNotFound",
                    MailImportStatus.StateMismatch => "ErrorIrresolvableConflict",
                    MailImportStatus.RequestTooLarge => "ErrorExceededFindCountLimit",
                    _ => outcome is null ? throw new InvalidOperationException("The EWS create outcome is missing.") : Failure(outcome.Error),
                };
            }
            var message = new XElement(GatewayEwsSoap.Messages + "CreateItemResponseMessage", new XAttribute("ResponseClass", code is null ? "Success" : "Error"));
            if (code is not null) message.Add(new XElement(GatewayEwsSoap.Messages + "MessageText", "The mail-item creation was refused."));
            message.Add(new XElement(GatewayEwsSoap.Messages + "ResponseCode", code ?? "NoError"));
            if (code is not null) message.Add(new XElement(GatewayEwsSoap.Messages + "DescriptiveLinkKey", 0));
            var items = new XElement(GatewayEwsSoap.Messages + "Items");
            if (code is null) items.Add(new XElement(GatewayEwsSoap.Types + "Message", new XElement(GatewayEwsSoap.Types + "ItemId",
                new XAttribute("Id", GatewayEwsItemIdCodec.Encode(account, outcome!.EmailId!.Value)),
                new XAttribute("ChangeKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(result!.NewState!))))));
            message.Add(items);
            responses.Add(message);
        }
        return GatewayEwsSoap.Envelope(new XElement(GatewayEwsSoap.Messages + "CreateItemResponse", responses));
    }

    private static string? Failure(MailImportItemError error) => error switch
    {
        MailImportItemError.None => null,
        MailImportItemError.MissingBlob => "ErrorServerBusy",
        MailImportItemError.InvalidMailbox => "ErrorFolderNotFound",
        MailImportItemError.TooLarge => "ErrorDataSizeLimitExceeded",
        MailImportItemError.OverQuota => "ErrorQuotaExceeded",
        MailImportItemError.InvalidEmail => "ErrorInvalidMimeContent",
        _ => throw new InvalidOperationException("The EWS creation failure is invalid."),
    };
}
