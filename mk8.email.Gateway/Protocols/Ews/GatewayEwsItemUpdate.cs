using System.Text;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemUpdate
{
    public static async Task<string> ExecuteAsync(GatewayEwsClient application, ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, GatewayEwsRequest request, CancellationToken cancellationToken)
    {
        var references = request.Items ?? throw new InvalidOperationException("The EWS update references are incomplete.");
        var states = request.ReadStates ?? throw new InvalidOperationException("The EWS update values are incomplete.");
        if (references.Count != states.Count) throw new InvalidOperationException("The EWS update values do not correlate.");
        var plans = Prepare(references, states, account);
        var ids = plans.Where(plan => plan.Code is null).Select(plan => plan.Id).Distinct().ToArray();
        if (ids.Length == 0) return GatewayEwsItemUpdateResponse.Render(account, plans, null);
        if (ids.Length > profile.Limits.MaxObjectsInGet || plans.Count(plan => plan.Code is null) > profile.Limits.MaxObjectsInSet)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        var read = await application.ReadItemsAsync(authentication, profile, account, ids, cancellationToken).ConfigureAwait(false);
        var snapshots = read.Messages.ToDictionary(item => item.MessageId, item => item.Value);
        var key = read.State is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(read.State));
        var seen = new HashSet<Guid>();
        for (var index = 0; index < plans.Length; index++)
        {
            if (plans[index].Code is not null) continue;
            var code = read.Status == MailMessageReadStatus.RequestTooLarge ? "ErrorDataSizeLimitExceeded"
                : !snapshots.ContainsKey(plans[index].Id) ? "ErrorItemNotFound"
                : references[index].ChangeKey is not null && !string.Equals(references[index].ChangeKey, key, StringComparison.Ordinal)
                    ? "ErrorIrresolvableConflict"
                : !seen.Add(plans[index].Id) ? "ErrorInvalidRequest" : null;
            plans[index] = plans[index] with { Code = code };
        }
        var updates = plans.Where(plan => plan.Code is null).Select(plan => new MailMessageUpdate($"E{plan.Id:N}", Patch(plan.Read))).ToArray();
        if (updates.Length == 0) return GatewayEwsItemUpdateResponse.Render(account, plans, null);
        var changes = plans.Where(plan => plan.Code is null).ToDictionary(plan => $"E{plan.Id:N}",
            plan => WouldChange(snapshots[plan.Id], plan.Read), StringComparer.Ordinal);
        // One-key flag changes preserve all other flags and custom keywords.
        // The admitted snapshot guards the entire native mutation transaction,
        // including clients that omitted an optional ChangeKey.
        var command = new MailMessageMutationCommand(account, read.State!, [], updates, []);
        var reply = await application.ExecuteOperationAsync(authentication, profile, MailOperationKind.MutateMessages,
            command, cancellationToken).ConfigureAwait(false);
        return GatewayEwsItemUpdateResponse.Render(account, plans, GatewayEwsItemUpdateReply.Decode(reply, command, changes));
    }

    private static bool WouldChange(MailMessageSnapshot snapshot, bool read)
    {
        var keywords = snapshot.Stored!.Keywords;
        if (keywords is null || keywords.Any(key => string.IsNullOrEmpty(key) || key.Any(char.IsControl))
            || keywords.Distinct(StringComparer.Ordinal).Count() != keywords.Count)
            throw new InvalidOperationException("The EWS update preflight keywords are invalid.");
        return keywords.Contains("$seen", StringComparer.Ordinal) != read;
    }

    private static MailMessagePatch Patch(bool read) => new([new(MailMessageFlagField.Keywords,
        read ? MailMessageFlagChangeKind.Set : MailMessageFlagChangeKind.Remove,
        [new("$seen", MailMessageFlagValue.Enabled)], false)], [], [], 0, false, null);

    private static GatewayEwsItemUpdatePlan[] Prepare(IReadOnlyList<GatewayEwsItemReference> references, IReadOnlyList<bool> states, Guid account)
    {
        var plans = new GatewayEwsItemUpdatePlan[references.Count];
        for (var index = 0; index < plans.Length; index++)
        {
            var reference = references[index];
            var code = !GatewayEwsItemIdCodec.TryDecode(reference.Id, out var owner, out var id) ? "ErrorInvalidIdMalformed"
                : account == Guid.Empty ? "ErrorItemNotFound" : owner != account ? "ErrorAccessDenied"
                : !ValidKey(reference.ChangeKey) ? "ErrorInvalidChangeKey" : null;
            plans[index] = new(id, states[index], code);
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
