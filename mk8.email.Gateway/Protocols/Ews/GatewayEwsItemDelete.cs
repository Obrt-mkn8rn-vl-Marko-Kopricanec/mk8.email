using System.Text;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemDelete
{
    public static async Task<string> ExecuteAsync(GatewayEwsClient application, ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, GatewayEwsRequest request, CancellationToken cancellationToken)
    {
        var references = request.Items ?? throw new InvalidOperationException("The EWS delete request is incomplete.");
        var plans = Prepare(references, account);
        var ids = plans.Where(plan => plan.Code is null).Select(plan => plan.Id).ToArray();
        if (ids.Length == 0) return Render(plans);
        if (ids.Length > Math.Min(profile.Limits.MaxObjectsInGet, profile.Limits.MaxObjectsInSet))
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        var read = await application.ReadItemsAsync(authentication, profile, account, ids, cancellationToken).ConfigureAwait(false);
        var byId = read.Messages.ToDictionary(item => item.MessageId);
        var key = read.State is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(read.State));
        for (var index = 0; index < plans.Length; index++)
        {
            if (plans[index].Code is not null) continue;
            var code = read.Status == MailMessageReadStatus.RequestTooLarge ? "ErrorDataSizeLimitExceeded"
                : !byId.ContainsKey(plans[index].Id) ? "ErrorItemNotFound"
                : references[index].ChangeKey is not null && !string.Equals(references[index].ChangeKey, key, StringComparison.Ordinal)
                    ? "ErrorIrresolvableConflict" : null;
            plans[index] = plans[index] with { Code = code };
        }
        var destroys = plans.Where(plan => plan.Code is null).Select(plan => new MailMessageDestroy($"E{plan.Id:N}")).ToArray();
        if (destroys.Length == 0) return Render(plans);
        // The Worker compares this admitted email state atomically, even when
        // the client omitted ChangeKey. IDs never supply account authority.
        var command = new MailMessageMutationCommand(account, read.State!, [], [], destroys);
        var reply = await application.ExecuteOperationAsync(authentication, profile, MailOperationKind.MutateMessages,
            command, cancellationToken).ConfigureAwait(false);
        Apply(plans, GatewayEwsItemDeleteReply.Decode(reply, command));
        return Render(plans);
    }

    private static Plan[] Prepare(IReadOnlyList<GatewayEwsItemReference> references, Guid account)
    {
        var result = new Plan[references.Count];
        var seen = new HashSet<Guid>();
        for (var index = 0; index < result.Length; index++)
        {
            var reference = references[index];
            var code = !GatewayEwsItemIdCodec.TryDecode(reference.Id, out var requestedAccount, out var id) ? "ErrorInvalidIdMalformed"
                : account == Guid.Empty ? "ErrorItemNotFound"
                : requestedAccount != account ? "ErrorAccessDenied"
                : !ValidKey(reference.ChangeKey) ? "ErrorInvalidChangeKey"
                : !seen.Add(id) ? "ErrorInvalidRequest" : null;
            result[index] = new(id, code);
        }
        return result;
    }

    private static bool ValidKey(string? key)
    {
        if (key is null) return true;
        Span<byte> bytes = stackalloc byte[1024];
        return key.Length <= 2048 && Convert.TryFromBase64String(key, bytes, out var count) && count > 0
            && string.Equals(key, Convert.ToBase64String(bytes[..count]), StringComparison.Ordinal);
    }

    private static void Apply(Plan[] plans, MailMessageMutationResult result)
    {
        var outcomes = result.Destroyed.ToDictionary(item => item.RequestedId, StringComparer.Ordinal);
        foreach (ref var plan in plans.AsSpan())
        {
            if (plan.Code is not null) continue;
            var code = result.Status switch
            {
                MailMessageMutationStatus.StateMismatch => "ErrorIrresolvableConflict",
                MailMessageMutationStatus.AccountNotFound => "ErrorItemNotFound",
                _ => outcomes[$"E{plan.Id:N}"].Failure is null ? null : "ErrorItemNotFound",
            };
            plan = plan with { Code = code };
        }
    }

    private static string Render(IEnumerable<Plan> plans)
    {
        var messages = GatewayEwsSoap.Messages;
        var responses = new XElement(messages + "ResponseMessages");
        foreach (var plan in plans)
        {
            var response = new XElement(messages + "DeleteItemResponseMessage",
                new XAttribute("ResponseClass", plan.Code is null ? "Success" : "Error"));
            if (plan.Code is not null) response.Add(new XElement(messages + "MessageText", "The mail-item deletion was refused."));
            response.Add(new XElement(messages + "ResponseCode", plan.Code ?? "NoError"));
            if (plan.Code is not null) response.Add(new XElement(messages + "DescriptiveLinkKey", 0));
            responses.Add(response);
        }
        return GatewayEwsSoap.Envelope(new XElement(messages + "DeleteItemResponse", responses));
    }

    private sealed record Plan(Guid Id, string? Code);
}
