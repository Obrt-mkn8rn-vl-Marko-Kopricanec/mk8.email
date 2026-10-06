using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.MailWire;

namespace mk8.email.Gateway.Protocols.Ews;

internal sealed class GatewayEwsClient(IGatewayApplicationTransport transport, EnvironmentConfig environment)
{
    internal const int MaximumGraphSize = 500;
    // Eleven fixed ApplicationValue members, bounded domain strings and numeric
    // counters fit this conservative per-row encoded budget, including key text.
    internal const int EncodedFolderBudgetBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<JmapApplicationProfile?> AuthenticateAsync(ProtocolAuthentication authentication,
        CancellationToken cancellationToken)
    {
        var result = await transport.SendAsync<JmapProfileApplicationRequest, JmapApplicationResult>("ews",
            ApplicationOperations.JmapProfileGet, new(authentication), cancellationToken).ConfigureAwait(false);
        if (string.Equals(result.Outcome, JmapApplicationOutcomes.Unauthorized, StringComparison.Ordinal)) return null;
        if (!string.Equals(result.Outcome, JmapApplicationOutcomes.Ok, StringComparison.Ordinal) || result.Failure is not null || result.Profile is null
            || result.OperationResult is not null || result.Content is not null) Invalid();
        var profile = result.Profile;
        if (!SmtpAddress.TryNormalize(profile.Username, allowEmpty: false, out _)
            || profile.Accounts is null || profile.Limits is null || profile.Limits.MaxObjectsInGet <= 0 || profile.Limits.MaxObjectsInSet <= 0
            || profile.Accounts.Any(account => account is null || !TryAccount(account.Id, out _)
                || !SmtpAddress.TryNormalize(account.Name, allowEmpty: false, out _))
            || profile.Accounts.Select(account => account.Id).Distinct(StringComparer.Ordinal).Count() != profile.Accounts.Length)
            Invalid();
        return profile;
    }

    public async Task<MailFolderReadResult> ReadGraphAsync(ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, CancellationToken cancellationToken)
    {
        // Count first: never request an unbounded encoded snapshot when the Worker
        // is configured to permit more objects than this presentation implements.
        var count = await ExecuteAsync<MailFolderQueryCommand>(authentication, profile, MailOperationKind.FindFolders,
            new(account, new(null, [], false, false), false, 0, null, false, 0, 0), cancellationToken).ConfigureAwait(false);
        RequireMembers(count, "status", "state", "position", "ids", "total");
        var query = count.Deserialize<MailFolderQueryResult>(JsonOptions) ?? throw InvalidResult();
        if (query.Ids is null || query.Ids.Count != 0 || query.Position != 0 || query.Total < 0
            || query.Status is not (MailFolderQueryStatus.Ok or MailFolderQueryStatus.AccountNotFound)) Invalid();
        if (query.Status == MailFolderQueryStatus.AccountNotFound)
        {
            if (query.State is not null || query.Total != 0) Invalid();
            return new(MailFolderReadStatus.AccountNotFound, null, []);
        }
        var limit = MaximumFolders(profile, environment.Messaging.MaxPayloadBytes);
        if (query.Total > limit) return new(MailFolderReadStatus.RequestTooLarge, null, []);
        RequireState(query.State);
        var data = await ExecuteAsync<MailFolderReadCommand>(authentication, profile, MailOperationKind.ReadFolders,
            new(account, null, false), cancellationToken).ConfigureAwait(false);
        RequireMembers(data, "status", "state", "folders");
        var folders = data["folders"] as JsonArray ?? throw InvalidResult();
        foreach (var folder in folders)
        {
            var snapshot = folder as JsonObject ?? throw InvalidResult();
            RequireMembers(snapshot, "id", "name", "parentId", "role", "sortOrder", "isSubscribed", "totalEmails",
                "unreadEmails", "totalThreads", "unreadThreads", "isProtected");
        }
        var read = data.Deserialize<MailFolderReadResult>(JsonOptions) ?? throw InvalidResult();
        if (read.Folders is null || !Enum.IsDefined(read.Status)) Invalid();
        if (read.Status != MailFolderReadStatus.Ok)
        {
            if (read.Folders.Count != 0 || read.State is not null) Invalid();
            return read;
        }
        RequireState(read.State);
        if (read.Folders.Count > limit) return new(MailFolderReadStatus.RequestTooLarge, null, []);
        if (read.Folders.Count != query.Total || !string.Equals(read.State, query.State, StringComparison.Ordinal))
            throw new GatewayEwsRequestException("ErrorServerBusy", status: StatusCodes.Status503ServiceUnavailable);
        GatewayEwsFolderGraph.Validate(read.Folders);
        return read;
    }

    private async Task<JsonObject> ExecuteAsync<T>(ProtocolAuthentication authentication, JmapApplicationProfile profile,
        MailOperationKind operation, T command, CancellationToken cancellationToken)
    {
        var response = await ExecuteOperationAsync(authentication, profile, operation, command, cancellationToken).ConfigureAwait(false);
        if (response.KnownEntities.Count != 0) Invalid();
        return ApplicationValueCodec.Decode(response.Response.Data!) as JsonObject ?? throw InvalidResult();
    }

    internal async Task<MailOperationResult> ExecuteOperationAsync<T>(ProtocolAuthentication authentication, JmapApplicationProfile profile,
        MailOperationKind operation, T command, CancellationToken cancellationToken)
    {
        var arguments = JsonSerializer.SerializeToNode(command, JsonOptions) as JsonObject ?? throw InvalidResult();
        var request = new MailOperationApplicationRequest(authentication,
            new([MailFeature.Basic, MailFeature.Messages], operation, arguments, new Dictionary<string, string>(StringComparer.Ordinal)));
        var result = await transport.SendAsync<MailOperationApplicationRequest, JmapApplicationResult>("ews",
            ApplicationOperations.MailOperationExecute, request, cancellationToken).ConfigureAwait(false);
        if (string.Equals(result.Outcome, JmapApplicationOutcomes.Unauthorized, StringComparison.Ordinal))
            throw new GatewayEwsRequestException("ErrorAccessDenied", status: StatusCodes.Status401Unauthorized);
        var response = result.OperationResult;
        if (!string.Equals(result.Outcome, JmapApplicationOutcomes.Ok, StringComparison.Ordinal) || result.Failure is not null || response is null
            || response.Response is null || response.Response.Operation != operation || response.Response.Data is null
            || response.Response.AdditionalResults is not null || response.Profile is null
            || response.KnownEntities is null
            || !string.Equals(response.Profile.Username, profile.Username, StringComparison.Ordinal)
            || response.Profile.Accounts is null
            || !response.Profile.Accounts.Select(account => account.Id).SequenceEqual(profile.Accounts.Select(account => account.Id), StringComparer.Ordinal))
            Invalid();
        return response;
    }

    internal static int MaximumFolders(JmapApplicationProfile profile, int payloadBytes)
    {
        var profileBytes = JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions).Length;
        // Reserve the shared metadata allowance for wrapper/state/authentication,
        // then account for the profile that accompanies every operation reply.
        var capacity = Math.Max(0, (payloadBytes - GatewayHttpPayloadBudget.MetadataBytes - profileBytes) / EncodedFolderBudgetBytes);
        return Math.Min(MaximumGraphSize, Math.Min(profile.Limits.MaxObjectsInGet, capacity));
    }

    internal static bool TryAccount(string value, out Guid account)
    {
        account = Guid.Empty;
        return value.Length == 33 && value[0] == 'A' && Guid.TryParseExact(value.AsSpan(1), "N", out account)
            && account != Guid.Empty;
    }

    private static void RequireMembers(JsonObject data, params string[] names)
    {
        if (data.Count != names.Length || names.Any(name => !data.ContainsKey(name))) Invalid();
    }

    private static void RequireState(string? state)
    {
        if (string.IsNullOrEmpty(state) || state.Length > 256 || state.Any(char.IsControl)) Invalid();
    }

    private static InvalidOperationException InvalidResult() => new("The EWS application result is invalid.");

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw InvalidResult();
}
