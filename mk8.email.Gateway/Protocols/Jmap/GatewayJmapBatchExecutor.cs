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
                    invocation, arguments, knownEntities, profile.Limits.MaxObjectsInGet,
                    profile.Limits.MaxObjectsInSet, cancellationToken)
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
        int maximumObjectsInSet,
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
        if (!TryPrepareTypedOperation(invocation.Operation, arguments, knownEntities, features, maximumObjects,
                maximumObjectsInSet, out var typedOperation, out var failure))
            return new(failure ?? "invalidArguments", null, null);
        var payload = typedOperation?.Payload ?? arguments;
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
            DecodePrimary(operation.Response, typedOperation),
        };
        if (operation.Response.AdditionalResults is not null)
        {
            foreach (var additional in operation.Response.AdditionalResults)
                displayed.Add((additional.Operation, (JsonObject)ApplicationValueCodec.Decode(additional.Data)!));
        }
        return new(null, result, displayed);
    }

    private static bool TryPrepareTypedOperation(
        MailOperationKind operation,
        JsonObject arguments,
        IReadOnlyDictionary<string, string> knownEntities,
        IReadOnlyList<MailFeature> features,
        int maximumObjects,
        int maximumObjectsInSet,
        out TypedOperationSelection? selection,
        out string? failure)
    {
        selection = null;
        failure = null;
        if (operation == MailOperationKind.ReadFolders && features.Contains(MailFeature.Messages))
        {
            if (!GatewayMailboxGetCodec.TryParse(arguments, maximumObjects, out var call, out failure)) return false;
            selection = Select<MailFolderReadCommand, MailFolderReadResult>(operation, call!.Command,
                result => GatewayMailboxGetCodec.Render(call, result));
        }
        else if (MailChangeOperations.TryGetFeature(operation, out var changeFeature)
            && features.Contains(changeFeature))
        {
            if (!GatewayMailChangesCodec.TryParse(arguments, operation, out var call, out failure)) return false;
            selection = Select<MailChangesCommand, MailChangesResult>(operation, call!.Command,
                result => GatewayMailChangesCodec.Render(call, result));
        }
        else if (operation == MailOperationKind.ReadAddressBooks && features.Contains(MailFeature.Contacts))
        {
            if (!GatewayAddressBookGetCodec.TryParse(arguments, maximumObjects, out var call, out failure)) return false;
            selection = Select<MailAddressBookReadCommand, MailAddressBookReadResult>(operation, call!.Command,
                result => GatewayAddressBookGetCodec.Render(call, result));
        }
        else if (operation == MailOperationKind.ReadSenderIdentities && features.Contains(MailFeature.Submission))
        {
            if (!GatewayIdentityGetCodec.TryParse(arguments, maximumObjects, out var call, out failure)) return false;
            selection = Select<MailIdentityReadCommand, MailIdentityReadResult>(operation, call!.Command,
                result => GatewayIdentityGetCodec.Render(call, result));
        }
        else if (operation == MailOperationKind.ReadVacationSettings && features.Contains(MailFeature.AutomaticReplies))
        {
            if (!GatewayVacationGetCodec.TryParse(arguments, maximumObjects, out var call, out failure)) return false;
            selection = Select<MailVacationReadCommand, MailVacationReadResult>(operation, call!.Command,
                result => GatewayVacationGetCodec.Render(call, result));
        }
        else if (operation == MailOperationKind.ReadNotificationSubscriptions && features.Contains(MailFeature.Basic))
        {
            if (!GatewayPushSubscriptionGetCodec.TryParse(arguments, maximumObjects, out var call, out failure)) return false;
            selection = Select<MailPushSubscriptionReadCommand, MailPushSubscriptionReadResult>(operation, call!.Command,
                result => GatewayPushSubscriptionGetCodec.Render(call, result));
        }
        else if (operation == MailOperationKind.ReadThreads && features.Contains(MailFeature.Messages))
        {
            if (!GatewayThreadGetCodec.TryParse(arguments, maximumObjects, out var call, out failure)) return false;
            selection = Select<MailThreadReadCommand, MailThreadReadResult>(operation, call!.Command,
                result => GatewayThreadGetCodec.Render(call, result));
        }
        else if (operation == MailOperationKind.ReadSubmissions && features.Contains(MailFeature.Submission))
        {
            if (!GatewaySubmissionGetCodec.TryParse(arguments, maximumObjects, out var call, out failure)) return false;
            selection = Select<MailSubmissionReadCommand, MailSubmissionReadResult>(operation, call!.Command,
                result => GatewaySubmissionGetCodec.Render(call, result));
        }
        else
            return TryPrepareTypedExtendedOperation(operation, arguments, knownEntities, features,
                maximumObjects, maximumObjectsInSet, out selection, out failure);
        return true;
    }

    private static bool TryPrepareTypedExtendedOperation(
        MailOperationKind operation,
        JsonObject arguments,
        IReadOnlyDictionary<string, string> knownEntities,
        IReadOnlyList<MailFeature> features,
        int maximumObjectsInGet,
        int maximumObjectsInSet,
        out TypedOperationSelection? selection,
        out string? failure)
    {
        selection = null;
        failure = null;
        if (operation == MailOperationKind.CopyBinaryObjects && features.Contains(MailFeature.Basic))
        {
            if (!GatewayBlobCopyCodec.TryParse(arguments, maximumObjectsInSet, out var call, out failure)) return false;
            selection = Select<MailBlobCopyCommand, MailBlobCopyResult>(operation, call!.Command,
                result => GatewayBlobCopyCodec.Render(call, result));
        }
        else if (operation == MailOperationKind.MutateVacationSettings && features.Contains(MailFeature.AutomaticReplies))
        {
            if (!GatewayVacationSetCodec.TryParse(arguments, knownEntities, maximumObjectsInSet, out var call, out failure)) return false;
            selection = Select<MailVacationSetCommand, MailVacationSetResult>(operation, call!.Command,
                result => GatewayVacationSetCodec.Render(call, result));
        }
        else if (operation == MailOperationKind.FindSubmissions && features.Contains(MailFeature.Submission))
        {
            if (!GatewaySubmissionQueryCodec.TryParseQuery(arguments, maximumObjectsInGet, out var call, out failure)) return false;
            selection = Select<MailSubmissionQueryCommand, MailSubmissionQueryResult>(operation, call!.Command,
                result => GatewaySubmissionQueryCodec.RenderQuery(call, result));
        }
        else if (operation == MailOperationKind.FindSubmissionChanges && features.Contains(MailFeature.Submission))
        {
            if (!GatewaySubmissionQueryCodec.TryParseChanges(arguments, out var call, out failure)) return false;
            selection = Select<MailSubmissionQueryChangesCommand, MailSubmissionQueryChangesResult>(operation, call!.Command,
                result => GatewaySubmissionQueryCodec.RenderChanges(call, result));
        }
        return true;
    }

    private static TypedOperationSelection Select<TCommand, TResult>(
        MailOperationKind operation,
        TCommand command,
        Func<TResult, (MailOperationKind Operation, JsonObject Data)> render) =>
        new(operation,
            JsonSerializer.SerializeToNode(command, FolderJsonOptions)?.AsObject()
                ?? throw new InvalidOperationException("Could not encode the typed operation command."),
            data => render(data.Deserialize<TResult>(FolderJsonOptions)
                ?? throw new InvalidOperationException("The Application returned an incomplete typed operation result.")));

    private static (MailOperationKind Operation, JsonObject Data) DecodePrimary(
        MailOperationResponse response,
        TypedOperationSelection? selection)
    {
        if (selection is null)
            return (response.Operation, (JsonObject)ApplicationValueCodec.Decode(response.Data)!);
        if (response.AdditionalResults is not null)
            throw new InvalidOperationException("A typed operation returned unexpected additional results.");
        if (response.Operation == MailOperationKind.Failure)
            return (response.Operation, (JsonObject)ApplicationValueCodec.Decode(response.Data)!);
        if (response.Operation != selection.Operation)
            throw new InvalidOperationException("The Application returned a different typed operation.");
        return selection.Render((JsonObject)ApplicationValueCodec.Decode(response.Data)!);
    }

    private sealed record TypedOperationSelection(
        MailOperationKind Operation,
        JsonObject Payload,
        Func<JsonObject, (MailOperationKind Operation, JsonObject Data)> Render);

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
