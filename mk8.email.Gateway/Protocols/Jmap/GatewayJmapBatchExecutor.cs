using System.Text.Json.Nodes;
using System.Text.Json;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayJmapBatchExecutor
{
    private static readonly JsonSerializerOptions FolderJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 256,
    };

    public static async Task<Execution> ExecuteAsync(
        IGatewayJmapClient application,
        ProtocolAuthentication authentication,
        JmapApplicationBatch batch,
        JmapApplicationProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Invocations is null || batch.Features is null)
            throw NotRequest("The JMAP batch must contain features and invocations.");
        var invocations = CloneInvocations(batch.Invocations);
        var knownEntities = CloneCreatedIds(batch.CreatedIds);
        var responses = new List<JmapApplicationInvocation>();
        foreach (var invocation in invocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GatewayApplicationDeadline.ThrowIfExpired();
            if (!GatewayJmapArgumentBindingResolver.TryResolve(invocation, responses, out var arguments, out var failure))
                AddFailure(failure == ApplicationBindingFailure.InvalidTarget ? "invalidArguments" : "invalidResultReference");
            else if (invocation.Operation == MailOperationKind.None)
                AddFailure("unknownMethod");
            else if (invocation.Operation == MailOperationKind.Echo)
                AddResponse(invocation.Operation, arguments);
            else
            {
                var remote = await ExecuteRemoteAsync(application, authentication, batch.Features,
                    invocation, arguments, knownEntities, profile.Limits.MaxObjectsInGet, cancellationToken)
                    .ConfigureAwait(false);
                if (remote.LocalFailure is not null)
                    AddFailure(remote.LocalFailure);
                else
                {
                    var result = remote.Application
                        ?? throw new InvalidOperationException("The Application returned no mail operation result.");
                    if (!string.Equals(result.Outcome, JmapApplicationOutcomes.Ok, StringComparison.Ordinal)
                        || result.Failure is not null)
                        return new Execution(null, result);
                    foreach (var displayed in remote.Displayed!)
                        AddResponse(displayed.Operation, displayed.Data);
                    var operation = result.OperationResult!;
                    knownEntities = CloneCreatedIds(operation.KnownEntities);
                    profile = operation.Profile;
                }
            }

            void AddFailure(string type) => AddResponse(MailOperationKind.Failure, new JsonObject { ["type"] = type });

            void AddResponse(MailOperationKind operation, JsonObject data) =>
                responses.Add(new(operation, GatewayJmapJson.SanitizeResponse(data), invocation.CorrelationId));
        }
        cancellationToken.ThrowIfCancellationRequested();
        GatewayApplicationDeadline.ThrowIfExpired();
        return new Execution(new(responses.ToArray(), profile, batch.CreatedIds is null ? null : knownEntities), null);
    }

    private static async Task<RemoteInvocation> ExecuteRemoteAsync(
        IGatewayJmapClient application,
        ProtocolAuthentication authentication,
        IReadOnlyList<MailFeature> features,
        JmapApplicationCall invocation,
        JsonObject arguments,
        IReadOnlyDictionary<string, string> knownEntities,
        int maximumObjects,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> aliases;
        try
        {
            aliases = GatewayJmapReferenceAliasCodec.Collect(arguments);
        }
        catch (GatewayJmapBatchCodec.RequestException)
        {
            return new("invalidArguments", null, null);
        }
        GatewayMailboxGetCodec.Call? folderCall = null;
        JsonObject payload = arguments;
        if (invocation.Operation == MailOperationKind.ReadFolders && features.Contains(MailFeature.Messages))
        {
            if (!GatewayMailboxGetCodec.TryParse(arguments, maximumObjects, out folderCall, out var failure))
                return new(failure ?? "invalidArguments", null, null);
            payload = JsonSerializer.SerializeToNode(folderCall!.Command, FolderJsonOptions)?.AsObject()
                ?? throw new InvalidOperationException("Could not encode the folder read command.");
        }
        var result = await application.ExecuteOperationAsync(new(authentication,
            new MailOperationCommand(features, invocation.Operation, payload, aliases, knownEntities)),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.Outcome, JmapApplicationOutcomes.Ok, StringComparison.Ordinal)
            || result.Failure is not null)
            return new(null, result, null);
        var operation = result.OperationResult
            ?? throw new InvalidOperationException("The Application returned an incomplete mail operation result.");
        ValidateResponse(operation.Response);
        var displayed = new List<(MailOperationKind Operation, JsonObject Data)>
        {
            DecodePrimary(operation.Response, folderCall),
        };
        if (operation.Response.AdditionalResults is not null)
        {
            foreach (var additional in operation.Response.AdditionalResults)
                displayed.Add((additional.Operation, (JsonObject)ApplicationValueCodec.Decode(additional.Data)!));
        }
        return new(null, result, displayed);
    }

    private static (MailOperationKind Operation, JsonObject Data) DecodePrimary(
        MailOperationResponse response,
        GatewayMailboxGetCodec.Call? folderCall)
    {
        if (folderCall is null)
            return (response.Operation, (JsonObject)ApplicationValueCodec.Decode(response.Data)!);
        if (response.AdditionalResults is not null)
            throw new InvalidOperationException("A folder read returned unexpected additional results.");
        if (response.Operation == MailOperationKind.Failure)
            return (response.Operation, (JsonObject)ApplicationValueCodec.Decode(response.Data)!);
        if (response.Operation != MailOperationKind.ReadFolders)
            throw new InvalidOperationException("The Application returned a different folder read operation.");
        var folderResult = ApplicationValueCodec.Decode(response.Data)?.Deserialize<MailFolderReadResult>(FolderJsonOptions)
            ?? throw new InvalidOperationException("The Application returned an incomplete folder read result.");
        return GatewayMailboxGetCodec.Render(folderCall, folderResult);
    }

    private sealed record RemoteInvocation(
        string? LocalFailure,
        JmapApplicationResult? Application,
        IReadOnlyList<(MailOperationKind Operation, JsonObject Data)>? Displayed);

    private static void ValidateResponse(MailOperationResponse response)
    {
        if (response is null || !Enum.IsDefined(response.Operation) || response.Operation == MailOperationKind.None
            || response.Data is null || ApplicationValueCodec.Decode(response.Data) is not JsonObject)
            throw new InvalidOperationException("The Application returned an invalid mail operation result.");
        if (response.AdditionalResults is not null)
        {
            foreach (var additional in response.AdditionalResults)
                ValidateResponse(additional);
        }
    }

    private static JmapApplicationCall[] CloneInvocations(JmapApplicationCall[] calls)
    {
        var invocations = new List<JmapApplicationCall>(calls.Length);
        foreach (var invocation in calls)
        {
            if (invocation is null || !Enum.IsDefined(invocation.Operation)
                || invocation.Operation == MailOperationKind.Failure
                || invocation.Arguments is null || invocation.CorrelationId is null)
                throw NotRequest("An application invocation is incomplete.");
            invocations.Add(invocation with
            {
                Arguments = (JsonObject)invocation.Arguments.DeepClone(),
                Bindings = CloneBindings(invocation.Bindings),
            });
        }
        return invocations.ToArray();
    }

    private static Dictionary<string, string> CloneCreatedIds(IReadOnlyDictionary<string, string>? values)
    {
        var createdIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values is not null)
        {
            foreach (var item in values)
            {
                if (item.Value is null || !GatewayJmapBatchCodec.IsId(item.Key) || !GatewayJmapBatchCodec.IsId(item.Value))
                    throw NotRequest("The createdIds property contains an invalid creation id or object id.");
                createdIds.Add(item.Key, item.Value);
            }
        }
        return createdIds;
    }

    private static ApplicationArgumentBinding[]? CloneBindings(ApplicationArgumentBinding[]? bindings)
    {
        if (bindings is null)
            return null;
        var result = new ApplicationArgumentBinding[bindings.Length];
        for (var index = 0; index < bindings.Length; index++)
        {
            var binding = bindings[index];
            if (binding is null || binding.Target is null || binding.SourceCorrelationId is null
                || !Enum.IsDefined(binding.SourceOperation) || binding.Path is null || !Enum.IsDefined(binding.Failure))
                throw NotRequest("An application argument binding is incomplete.");
            var path = new ApplicationValuePathSegment[binding.Path.Length];
            for (var segmentIndex = 0; segmentIndex < path.Length; segmentIndex++)
            {
                var segment = binding.Path[segmentIndex];
                if (segment is null || segment.Property is null || segment.ArrayIndex is < 0
                    || segment.ArrayIndex is not null && segment.AllArrayItems)
                    throw NotRequest("An application value selector is incomplete or ambiguous.");
                path[segmentIndex] = segment;
            }
            result[index] = binding with { Path = path };
        }
        return result;
    }

    private static GatewayJmapBatchCodec.RequestException NotRequest(string detail) => new(detail);

    internal sealed record Execution(JmapApplicationBatchResult? Batch, JmapApplicationResult? Failure);
}
