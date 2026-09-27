using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using mk8.email.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

public sealed class JmapRequestProcessor
{
    private readonly IReadOnlyDictionary<string, IJmapMethod> _methods;
    private readonly JmapAccountProfileService _sessions;
    private readonly EmailDbContext _database;
    private readonly EnvironmentConfig _environment;
    private readonly LargeObjectTransactionEffects _blobEffects;
    private readonly ILogger<JmapRequestProcessor> _logger;
    private readonly ApplicationOperationReceiptStore? _receipts;
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    public JmapRequestProcessor(
        IEnumerable<IJmapMethod> methods,
        JmapAccountProfileService sessions,
        EmailDbContext database,
        EnvironmentConfig environment,
        LargeObjectTransactionEffects blobEffects,
        ILogger<JmapRequestProcessor> logger,
        ApplicationOperationReceiptStore? receipts = null)
    {
        _methods = methods.ToDictionary(method => method.Name, StringComparer.Ordinal);
        _sessions = sessions;
        _database = database;
        _environment = environment;
        _blobEffects = blobEffects;
        _logger = logger;
        _receipts = receipts;
    }

    public Task<JmapApplicationBatchResult> ProcessAsync(
        JmapApplicationBatch batch,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken = default) => ProcessAsync(batch, user, null, cancellationToken);

    public async Task<JmapApplicationBatchResult> ProcessAsync(
        JmapApplicationBatch batch,
        AuthenticatedMailUser user,
        Guid? operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(user);
        if (batch.Invocations is null)
            throw NotRequest("The application batch must contain invocations.");
        var capabilities = ValidateHeader(batch.Capabilities, batch.Invocations.Length);
        var invocations = CloneInvocations(batch.Invocations);
        var createdIds = CloneCreatedIds(batch.CreatedIds);
        var context = new JmapInvocationContext(user, capabilities, createdIds);
        var responses = new List<JmapApplicationInvocation>();
        var relational = _database.Database.IsRelational();
        if (relational && (operationId is null || operationId == Guid.Empty || _receipts is null))
            throw new InvalidOperationException("A durable operation identity and receipt store are required.");
        var inputHash = relational
            ? Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(batch, ReceiptJsonOptions)))
            : string.Empty;

        for (var step = 0; step < invocations.Count; step++)
        {
            var invocation = invocations[step];
            var receiptKey = relational
                ? new ApplicationReceiptKey(operationId!.Value, step, user.Id, "jmap.batch", inputHash)
                : null;
            JmapMethodResponse response;
            if (!ApplicationArgumentBindingResolver.TryResolve(
                    invocation,
                    responses,
                    out var resolvedArguments,
                    out var referenceFailure))
                response = JmapMethodResponse.Error(referenceFailure == ApplicationBindingFailure.InvalidTarget
                    ? "invalidArguments" : "invalidResultReference");
            else if (!_methods.TryGetValue(invocation.Name, out var method)
                || !capabilities.Contains(method.Capability))
                response = JmapMethodResponse.Error("unknownMethod");
            else
                response = await InvokeAtomicallyAsync(
                    method, context, resolvedArguments, receiptKey, cancellationToken).ConfigureAwait(false);

            AddResponse(response);
            if (response.AdditionalResponses is not null)
            {
                foreach (var additional in response.AdditionalResponses)
                    AddResponse(additional);
            }

            void AddResponse(JmapMethodResponse completed)
            {
                var arguments = JmapJson.SanitizeResponse(completed.Arguments);
                responses.Add(new JmapApplicationInvocation(completed.Name, arguments, invocation.CorrelationId));
            }
        }

        var profile = await _sessions.GetProfileAsync(user, cancellationToken).ConfigureAwait(false);
        return new JmapApplicationBatchResult(
            responses.ToArray(), profile, batch.CreatedIds is null ? null : createdIds);
    }

    private static List<JmapApplicationCall> CloneInvocations(JmapApplicationCall[] calls)
    {
        var invocations = new List<JmapApplicationCall>(calls.Length);
        foreach (var invocation in calls)
        {
            if (invocation is null || invocation.Name is null
                || invocation.Arguments is null || invocation.CorrelationId is null)
                throw NotRequest("An application invocation is incomplete.");
            invocations.Add(invocation with
            {
                Arguments = (JsonObject)invocation.Arguments.DeepClone(),
                Bindings = CloneBindings(invocation.Bindings),
            });
        }
        return invocations;
    }

    private static Dictionary<string, string> CloneCreatedIds(IReadOnlyDictionary<string, string>? values)
    {
        var createdIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values is not null)
        {
            foreach (var item in values)
            {
                if (item.Value is null || !JmapId.IsValidId(item.Key) || !JmapId.IsValidId(item.Value))
                    throw NotRequest("The createdIds property contains an invalid creation id or object id.");
                createdIds.Add(item.Key, item.Value);
            }
        }
        return createdIds;
    }

    internal void ValidatePreflight(JmapBatchPreflight? preflight)
    {
        if (preflight is not null)
            _ = ValidateHeader(preflight.Capabilities, preflight.InvocationCount);
    }

    private HashSet<string> ValidateHeader(string[] values, int invocationCount)
    {
        if (values is null || values.Any(capability => capability is null))
            throw NotRequest("The using property must contain capability strings.");
        var capabilities = values.ToHashSet(StringComparer.Ordinal);
        var supported = _methods.Values.Select(method => method.Capability)
            .Append(JmapConstants.CoreCapability).ToHashSet(StringComparer.Ordinal);
        var unknown = capabilities.FirstOrDefault(capability => !supported.Contains(capability));
        if (unknown is not null)
            throw new JmapRequestException(
                "urn:ietf:params:jmap:error:unknownCapability",
                "Unknown capability",
                $"The request uses an unsupported capability: {unknown}");
        if (!capabilities.Contains(JmapConstants.CoreCapability))
            throw NotRequest("The using property must include the JMAP core capability.");
        if (invocationCount < 0)
            throw NotRequest("The application invocation count cannot be negative.");
        if (invocationCount > _environment.Jmap.MaxCallsInRequest)
            throw new JmapRequestException(
                "urn:ietf:params:jmap:error:limit",
                "Request limit exceeded",
                "The request contains too many method calls.",
                "maxCallsInRequest");
        return capabilities;
    }

    private async Task<JmapMethodResponse> InvokeAtomicallyAsync(
        IJmapMethod method,
        JmapInvocationContext context,
        JsonObject arguments,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var createdIds = context.CreatedIds.ToDictionary(item => item.Key, item => item.Value);
        var postCommitMarker = context.MarkPostCommitActions();
        var blobEffectMarker = _blobEffects.Mark();
        var presentationMarker = context.MarkPresentationEffects();
        IDbContextTransaction? transaction = null;
        var commitAttempted = false;
        try
        {
            if (_database.Database.IsRelational())
                transaction = await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (receiptKey is not null)
            {
                var existing = await _receipts!.FindLockedAsync(receiptKey, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    var replay = JsonSerializer.Deserialize<JmapReplayState>(existing.Result.Span, ReceiptJsonOptions)
                        ?? throw new InvalidOperationException("The invocation receipt result is incomplete.");
                    context.CreatedIds.Clear();
                    foreach (var item in replay.CreatedIds)
                        context.CreatedIds[item.Key] = item.Value;
                    return replay.Response;
                }
            }

            var response = await method.InvokeAsync(context, arguments, cancellationToken).ConfigureAwait(false);
            if (MustRollBack(response))
            {
                await RollBackAsync(transaction).ConfigureAwait(false);
                await _blobEffects.RollbackAsync(blobEffectMarker).ConfigureAwait(false);
                RestoreInvocationState(context, createdIds, postCommitMarker);
                context.DiscardPresentationEffects(presentationMarker);
                return response;
            }

            if (receiptKey is not null)
            {
                response = NormalizeForReceipt(response);
                var result = JsonSerializer.SerializeToUtf8Bytes(new JmapReplayState(
                    response, context.CreatedIds.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)), ReceiptJsonOptions);
                await _receipts!.SaveAsync(receiptKey,
                    new ApplicationReceiptContent(result, context.PresentationEffectsSince(presentationMarker)),
                    cancellationToken).ConfigureAwait(false);
            }

            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            await _blobEffects.CommitAsync(blobEffectMarker).ConfigureAwait(false);
            var actions = context.TakePostCommitActions(postCommitMarker);
            foreach (var action in actions)
            {
                try
                {
                    // The database commit makes these actions durable work.
                    // A client disconnect must not prevent push verification
                    // (or any future committed external effect) from running.
                    await action(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "A JMAP post-commit action failed");
                }
            }
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollBackAsync(transaction).ConfigureAwait(false);
            await CompleteBlobRollbackAsync(blobEffectMarker, commitAttempted).ConfigureAwait(false);
            RestoreInvocationState(context, createdIds, postCommitMarker);
            context.DiscardPresentationEffects(presentationMarker);
            throw;
        }
        catch (Exception exception)
        {
            await RollBackAsync(transaction).ConfigureAwait(false);
            await CompleteBlobRollbackAsync(blobEffectMarker, commitAttempted).ConfigureAwait(false);
            RestoreInvocationState(context, createdIds, postCommitMarker);
            context.DiscardPresentationEffects(presentationMarker);
            _logger.LogError(exception, "JMAP method {MethodName} failed", method.Name);
            return JmapMethodResponse.Error("serverFail");
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
            context.DiscardPresentationEffects(presentationMarker);
        }
    }

    private static JmapMethodResponse NormalizeForReceipt(JmapMethodResponse response) => new(
        response.Name, JmapJson.SanitizeResponse(response.Arguments),
        response.AdditionalResponses?.Select(NormalizeForReceipt).ToArray());

    private async Task CompleteBlobRollbackAsync(int marker, bool commitAttempted)
    {
        if (commitAttempted)
        {
            // A failed commit can have an ambiguous outcome. Retaining an object is safer
            // than deleting content that a committed row may reference.
            _blobEffects.Discard(marker);
            return;
        }
        await _blobEffects.RollbackAsync(marker).ConfigureAwait(false);
    }

    private static bool MustRollBack(JmapMethodResponse response) =>
        response.Name == "error"
        && (!response.Arguments.TryGetPropertyValue("type", out var typeNode)
            || typeNode is not JsonValue typeValue
            || !typeValue.TryGetValue<string>(out var type)
            || type != "serverPartialFail");

    private async Task RollBackAsync(IDbContextTransaction? transaction)
    {
        if (transaction is null)
            return;
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not roll back a failed JMAP method");
        }
    }

    private void RestoreInvocationState(
        JmapInvocationContext context,
        IReadOnlyDictionary<string, string> createdIds,
        int postCommitMarker)
    {
        context.CreatedIds.Clear();
        foreach (var item in createdIds)
            context.CreatedIds[item.Key] = item.Value;
        context.DiscardPostCommitActions(postCommitMarker);
        _database.ChangeTracker.Clear();
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
                || binding.SourceName is null || binding.Path is null || !Enum.IsDefined(binding.Failure))
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

    private static JmapRequestException NotRequest(string detail) =>
        new(
            "urn:ietf:params:jmap:error:notRequest",
            "Invalid JMAP request",
            detail);

}
