using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemSyncReply
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly string[] Members = ["status", "oldState", "newState", "hasMoreChanges", "createdKeys", "updatedKeys", "destroyedKeys"];

    internal static MailChangesResult Decode(JsonObject data, string sinceState, int maximum)
    {
        if (data.Count != Members.Length || Members.Any(name => !data.ContainsKey(name))) Invalid();
        var result = data.Deserialize<MailChangesResult>(Options) ?? throw new InvalidOperationException("Invalid EWS item changes.");
        if (result.CreatedKeys is null || result.UpdatedKeys is null || result.DestroyedKeys is null
            || result.Status is not (MailChangesStatus.Ok or MailChangesStatus.AccountNotFound or MailChangesStatus.CannotCalculateChanges)
            || (long)result.CreatedKeys.Count + result.UpdatedKeys.Count + result.DestroyedKeys.Count > maximum) Invalid();
        var keys = result.CreatedKeys.Concat(result.UpdatedKeys).Concat(result.DestroyedKeys).ToArray();
        if (result.Status != MailChangesStatus.Ok)
        {
            if (result.OldState is not null || result.NewState is not null || result.HasMoreChanges || keys.Length != 0) Invalid();
            return result;
        }
        if (!string.Equals(result.OldState, sinceState, StringComparison.Ordinal)
            || !GatewayEwsFolderSyncState.TrySequence(sinceState, out var oldSequence)
            || !GatewayEwsFolderSyncState.TrySequence(result.NewState, out var newSequence) || newSequence < oldSequence
            || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || keys.Any(key => !TryItem(key, out _))
            || newSequence == oldSequence && (keys.Length != 0 || result.HasMoreChanges)
            || result.HasMoreChanges && keys.Length == 0) Invalid();
        return result;
    }

    internal static bool TryItem(string? key, out Guid id)
    {
        id = Guid.Empty;
        return key is { Length: 33 } && key[0] == 'E' && Guid.TryParseExact(key.AsSpan(1), "N", out id)
            && id != Guid.Empty && string.Equals(key, $"E{id:N}", StringComparison.Ordinal);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new InvalidOperationException("Invalid EWS item changes.");
}
