using System.Globalization;
using System.Text;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemCopy
{
    public static async Task<string> ExecuteAsync(GatewayEwsClient application, ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, GatewayEwsRequest request, CancellationToken cancellationToken)
    {
        var references = request.Items ?? throw new InvalidOperationException("The EWS copy request is incomplete.");
        var moving = request.Operation is "MoveItem";
        var plans = Prepare(references, account, moving);
        if (request.Folders.Count != 1) throw new InvalidOperationException("The EWS copy destination is incomplete.");
        var destination = request.Folders[0];
        var targetError = !GatewayEwsFolderIdCodec.TryDecode(destination.Id, out var targetAccount, out var target) ? "ErrorInvalidIdMalformed"
            : targetAccount != account ? "ErrorAccessDenied" : target == Guid.Empty ? "ErrorAccessDenied" : null;
        if (targetError is not null) return GatewayEwsItemCopyResponse.Render(request, account, plans, null, targetError);
        var ids = plans.Where(plan => plan.Code is null).Select(plan => plan.Id).Distinct().ToArray();
        if (ids.Length == 0) return GatewayEwsItemCopyResponse.Render(request, account, plans, null);
        if (ids.Length > profile.Limits.MaxObjectsInGet || plans.Count(plan => plan.Code is null) > profile.Limits.MaxObjectsInSet)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        var read = await application.ReadItemsAsync(authentication, profile, account, ids, cancellationToken).ConfigureAwait(false);
        var snapshots = read.Messages.ToDictionary(item => item.MessageId, item => item.Value);
        var key = read.State is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(read.State));
        var admitted = new HashSet<Guid>();
        for (var index = 0; index < plans.Length; index++)
        {
            if (plans[index].Code is not null) continue;
            var code = read.Status == MailMessageReadStatus.RequestTooLarge ? "ErrorDataSizeLimitExceeded"
                : !snapshots.ContainsKey(plans[index].Id) ? "ErrorItemNotFound"
                : references[index].ChangeKey is not null && !string.Equals(references[index].ChangeKey, key, StringComparison.Ordinal)
                    ? "ErrorIrresolvableConflict"
                : moving && snapshots[plans[index].Id].Stored!.FolderId == target ? "ErrorInvalidRequest"
                : moving && !admitted.Add(plans[index].Id) ? "ErrorItemNotFound" : null;
            plans[index] = plans[index] with { Code = code };
        }
        var items = plans.Where(plan => plan.Code is null).Select(plan => new MailCopyItem(plan.Token, plan.Id, false,
            target, MailMessageMailboxIssue.None, null, MailMessageKeywordIssue.None, null, false)).ToArray();
        if (items.Length == 0) return GatewayEwsItemCopyResponse.Render(request, account, plans, null);
        // Both operations compare the admitted account state before any writes.
        // Only MoveItem authorizes deletion of successfully copied sources in
        // this same transaction. A pre-copy state must not guard the later
        // deletion stage: creating the replacement has already changed it.
        var command = new MailCopyCommand(account, account, read.State!, read.State!, moving, null, items);
        var reply = await application.ExecuteOperationAsync(authentication, profile, MailOperationKind.CopyMessages,
            command, cancellationToken).ConfigureAwait(false);
        var result = GatewayEwsItemCopyReply.Decode(reply, command, snapshots);
        return GatewayEwsItemCopyResponse.Render(request, account, plans, result);
    }

    private static GatewayEwsItemCopyPlan[] Prepare(IReadOnlyList<GatewayEwsItemReference> references, Guid account, bool moving)
    {
        var plans = new GatewayEwsItemCopyPlan[references.Count];
        for (var index = 0; index < plans.Length; index++)
        {
            var reference = references[index];
            var code = !GatewayEwsItemIdCodec.TryDecode(reference.Id, out var requestedAccount, out var id) ? "ErrorInvalidIdMalformed"
                : account == Guid.Empty ? "ErrorItemNotFound" : requestedAccount != account ? "ErrorAccessDenied"
                : !ValidKey(reference.ChangeKey) ? "ErrorInvalidChangeKey" : null;
            plans[index] = new(id, (moving ? "ewsMove" : "ewsCopy") + index.ToString(CultureInfo.InvariantCulture), code);
        }
        return plans;
    }

    private static bool ValidKey(string? key)
    {
        if (key is null) return true;
        Span<byte> bytes = stackalloc byte[1024];
        return key.Length <= 2048 && Convert.TryFromBase64String(key, bytes, out var count) && count > 0
            && string.Equals(key, Convert.ToBase64String(bytes[..count]), StringComparison.Ordinal);
    }
}
