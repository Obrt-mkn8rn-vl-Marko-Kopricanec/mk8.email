using System.Text;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsFolderMove
{
    internal static async Task<string> ExecuteAsync(GatewayEwsClient application, ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, GatewayEwsRequest request, GatewayEwsFolderGraph? graph,
        string unavailable, CancellationToken cancellationToken)
    {
        if (request.Folders.Count is <= 0 or > GatewayEwsRequestParser.MaximumReferences || request.Folders.Count > profile.Limits.MaxObjectsInSet)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        var plans = GatewayEwsFolderMovePlan.Build(request, graph, profile.Username, unavailable, out var target);
        var updates = plans.Where(plan => plan.Code is null).Select(plan => new MailFolderUpdate($"M{plan.Id:N}",
            new(MailFolderFields.Parent, new(string.Empty, target == Guid.Empty ? null : $"M{target:N}", null, 0, false), [], null))).ToArray();
        if (updates.Length == 0) return Render(account, plans, null, null);
        // The graph state is REQUIRED even if the client omitted ChangeKeys.
        // No creates, destroys, name/role/settings patches or message writes.
        var command = new MailFolderMutationCommand(account, graph!.State, false, [], updates, []);
        var reply = await application.ExecuteOperationAsync(authentication, profile, MailOperationKind.MutateFolders,
            command, cancellationToken).ConfigureAwait(false);
        return Render(account, plans, Decode(reply, command), command);
    }

    internal static MailFolderMutationResult Decode(MailOperationResult reply, MailFolderMutationCommand command)
    {
        var result = GatewayEwsMutationReply.Decode(reply, command);
        if (result.Status != MailFolderMutationStatus.Ok) return result;
        for (var index = 0; index < command.Updates.Count; index++)
            if (!string.Equals(result.Updated[index].RequestedId, command.Updates[index].RequestedId, StringComparison.Ordinal)) throw Invalid();
        if (result.Updated.Any(item => item.FolderId is not null)
            && string.Equals(result.NewState, result.OldState, StringComparison.Ordinal)) throw Invalid();
        return result;
    }

    private static string Render(Guid account, GatewayEwsFolderMovePlan[] plans, MailFolderMutationResult? result,
        MailFolderMutationCommand? command)
    {
        var outcomes = result?.Updated.ToDictionary(item => item.RequestedId, StringComparer.Ordinal);
        var responses = new XElement(GatewayEwsSoap.Messages + "ResponseMessages");
        foreach (var plan in plans)
        {
            var code = plan.Code;
            if (code is null)
            {
                code = result!.Status switch
                {
                    MailFolderMutationStatus.AccountNotFound => "ErrorFolderNotFound",
                    MailFolderMutationStatus.StateMismatch => "ErrorIrresolvableConflict",
                    _ => outcomes![$"M{plan.Id:N}"].Failure?.Error switch
                    {
                        null => null,
                        MailFolderMutationError.NotFound => "ErrorFolderNotFound",
                        MailFolderMutationError.Forbidden => "ErrorAccessDenied",
                        _ => "ErrorInvalidRequest",
                    },
                };
            }
            var response = new XElement(GatewayEwsSoap.Messages + "MoveFolderResponseMessage",
                new XAttribute("ResponseClass", code is null ? "Success" : "Error"));
            if (code is not null) response.Add(new XElement(GatewayEwsSoap.Messages + "MessageText", "The mail-folder move was refused."));
            response.Add(new XElement(GatewayEwsSoap.Messages + "ResponseCode", code ?? "NoError"));
            if (code is not null) response.Add(new XElement(GatewayEwsSoap.Messages + "DescriptiveLinkKey", 0));
            else
            {
                if (command is null) throw Invalid();
                response.Add(new XElement(GatewayEwsSoap.Messages + "Folders", new XElement(GatewayEwsSoap.Types + "Folder",
                    new XElement(GatewayEwsSoap.Types + "FolderId", new XAttribute("Id", GatewayEwsFolderIdCodec.Encode(account, plan.Id)),
                        new XAttribute("ChangeKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(result!.NewState!)))))));
            }
            responses.Add(response);
        }
        return GatewayEwsSoap.Envelope(new XElement(GatewayEwsSoap.Messages + "MoveFolderResponse", responses));
    }

    private static InvalidOperationException Invalid() => new("The EWS folder move reply is invalid.");
}
