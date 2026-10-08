using System.Text;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemUpdateResponse
{
    public static string Render(Guid account, IEnumerable<GatewayEwsItemUpdatePlan> plans, MailMessageMutationResult? result)
    {
        var outcomes = result?.Updated.ToDictionary(item => item.RequestedId, StringComparer.Ordinal) ?? [];
        var responses = new XElement(GatewayEwsSoap.Messages + "ResponseMessages");
        foreach (var plan in plans)
        {
            var code = plan.Code;
            if (code is null)
            {
                code = result?.Status switch
                {
                    MailMessageMutationStatus.AccountNotFound => "ErrorItemNotFound",
                    MailMessageMutationStatus.StateMismatch => "ErrorIrresolvableConflict",
                    _ => Failure(outcomes[$"E{plan.Id:N}"].Failure),
                };
            }
            var message = new XElement(GatewayEwsSoap.Messages + "UpdateItemResponseMessage", new XAttribute("ResponseClass", code is null ? "Success" : "Error"));
            if (code is not null) message.Add(new XElement(GatewayEwsSoap.Messages + "MessageText", "The mail-item update was refused."));
            message.Add(new XElement(GatewayEwsSoap.Messages + "ResponseCode", code ?? "NoError"));
            if (code is not null) message.Add(new XElement(GatewayEwsSoap.Messages + "DescriptiveLinkKey", 0));
            var items = new XElement(GatewayEwsSoap.Messages + "Items");
            if (code is null) items.Add(new XElement(GatewayEwsSoap.Types + "Message", new XElement(GatewayEwsSoap.Types + "ItemId",
                new XAttribute("Id", GatewayEwsItemIdCodec.Encode(account, plan.Id)),
                new XAttribute("ChangeKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(result!.NewState!))))));
            message.Add(items);
            responses.Add(message);
        }
        return GatewayEwsSoap.Envelope(new XElement(GatewayEwsSoap.Messages + "UpdateItemResponse", responses));
    }

    private static string? Failure(MailMessageMutationFailure? failure) => failure?.Error switch
    {
        null => null,
        MailMessageMutationError.NotFound => "ErrorItemNotFound",
        MailMessageMutationError.InvalidProperties or MailMessageMutationError.TooManyKeywords => "ErrorInvalidPropertySet",
        _ => throw new InvalidOperationException("The EWS update failure is invalid."),
    };
}
