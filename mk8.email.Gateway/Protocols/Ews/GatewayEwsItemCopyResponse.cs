using System.Text;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemCopyResponse
{
    public static string Render(GatewayEwsRequest request, Guid account, IEnumerable<GatewayEwsItemCopyPlan> plans,
        MailCopyResult? result, string? targetError = null)
    {
        var outcomes = result?.Items.ToDictionary(item => item.CreationId, StringComparer.Ordinal) ?? [];
        var responses = new XElement(GatewayEwsSoap.Messages + "ResponseMessages");
        foreach (var plan in plans)
        {
            var code = plan.Code ?? targetError;
            MailCopyItemOutcome? outcome = null;
            if (code is null)
            {
                outcomes.TryGetValue(plan.Token, out outcome);
                code = result?.Status switch
                {
                    MailCopyStatus.StateMismatch => "ErrorIrresolvableConflict",
                    MailCopyStatus.SourceAccountNotFound => "ErrorItemNotFound",
                    MailCopyStatus.TargetAccountNotFound => "ErrorFolderNotFound",
                    MailCopyStatus.RequestTooLarge => "ErrorExceededFindCountLimit",
                    _ => outcome is null ? throw new InvalidOperationException("The EWS copy outcome is missing.") : Failure(outcome.Error),
                };
            }
            responses.Add(Response(request.Operation, account, code, outcome, result?.Destroy?.NewState ?? result?.NewTargetState, request.ReturnNewItemIds));
        }
        return GatewayEwsSoap.Envelope(new XElement(GatewayEwsSoap.Messages + request.Operation + "Response", responses));
    }

    private static XElement Response(string operation, Guid account, string? code, MailCopyItemOutcome? outcome, string? state, bool returnIds)
    {
        var messages = GatewayEwsSoap.Messages;
        var response = new XElement(messages + operation + "ResponseMessage", new XAttribute("ResponseClass", code is null ? "Success" : "Error"));
        if (code is not null) response.Add(new XElement(messages + "MessageText", operation is "MoveItem" ? "The mail-item move was refused." : "The mail-item copy was refused."));
        response.Add(new XElement(messages + "ResponseCode", code ?? "NoError"));
        if (code is not null) response.Add(new XElement(messages + "DescriptiveLinkKey", 0));
        var items = new XElement(messages + "Items");
        if (code is null && returnIds)
            items.Add(new XElement(GatewayEwsSoap.Types + "Message", new XElement(GatewayEwsSoap.Types + "ItemId",
                new XAttribute("Id", GatewayEwsItemIdCodec.Encode(account, outcome!.EmailId!.Value)),
                new XAttribute("ChangeKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(state!))))));
        response.Add(items);
        return response;
    }

    private static string? Failure(MailCopyItemError error) => error switch
    {
        MailCopyItemError.None => null,
        MailCopyItemError.NotFound => "ErrorItemNotFound",
        MailCopyItemError.InvalidMailbox => "ErrorFolderNotFound",
        MailCopyItemError.TooLarge => "ErrorDataSizeLimitExceeded",
        MailCopyItemError.OverQuota => "ErrorQuotaExceeded",
        MailCopyItemError.InvalidEmail => "ErrorInvalidPropertyRequest",
        _ => throw new InvalidOperationException("The EWS copy error is invalid."),
    };
}
