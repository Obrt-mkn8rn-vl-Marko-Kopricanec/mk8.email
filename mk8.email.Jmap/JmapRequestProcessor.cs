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
    private readonly IReadOnlyDictionary<MailOperationKind, IJmapMethod> _methods;
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
        _methods = methods.ToDictionary(method => ValidRegisteredOperation(method.Operation, method.Feature));
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
        var features = ValidateHeader(batch.Features, batch.Invocations.Length);
        var invocations = CloneInvocations(batch.Invocations);
        var createdIds = CloneCreatedIds(batch.CreatedIds);
        var context = new JmapInvocationContext(user, features, createdIds);
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
            else if (!_methods.TryGetValue(invocation.Operation, out var method)
                || !features.Contains(method.Feature))
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
                responses.Add(new JmapApplicationInvocation(completed.Operation, arguments, invocation.CorrelationId));
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
            _ = ValidateHeader(preflight.Features, preflight.InvocationCount);
    }

    private HashSet<MailFeature> ValidateHeader(MailFeature[] values, int invocationCount)
    {
        if (values is null || values.Any(feature => !Enum.IsDefined(feature)))
            throw NotRequest("The application batch contains invalid feature identifiers.");
        var features = values.ToHashSet();
        var supported = _methods.Values.Select(method => method.Feature)
            .Append(MailFeature.Basic).ToHashSet();
        if (features.Any(feature => !supported.Contains(feature)))
            throw new MailApplicationException(new MailApplicationFailure(
                MailFailureKind.UnsupportedFeature, "The requested mail features are not supported."));
        if (!features.Contains(MailFeature.Basic))
            throw NotRequest("The application batch must include the basic mail feature.");
        if (invocationCount < 0)
            throw NotRequest("The application invocation count cannot be negative.");
        if (invocationCount > _environment.Jmap.MaxCallsInRequest)
            throw new MailApplicationException(new MailApplicationFailure(
                MailFailureKind.ResourceLimit, "The application batch contains too many operations.",
                MailResourceLimit.OperationCount));
        return features;
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
                    ValidateResponse(replay.Response);
                    context.CreatedIds.Clear();
                    foreach (var item in replay.CreatedIds)
                        context.CreatedIds[item.Key] = item.Value;
                    return replay.Response;
                }
            }

            var response = await method.InvokeAsync(context, arguments, cancellationToken).ConfigureAwait(false);
            ValidateResponse(response);
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
            _logger.LogError(exception, "Mail operation {Operation} failed", method.Operation);
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
        response.Operation, JmapJson.SanitizeResponse(response.Arguments),
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
        response.Operation == MailOperationKind.Failure
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

    private static MailOperationKind ValidRegisteredOperation(MailOperationKind operation, MailFeature feature) =>
        Enum.IsDefined(operation) && operation is not (MailOperationKind.None or MailOperationKind.Failure)
            && Enum.IsDefined(feature) && feature != MailFeature.Unsupported
            ? operation : throw new ArgumentException("A handler must register a supported mail operation.", nameof(operation));

    private static void ValidateResponse(JmapMethodResponse response)
    {
        if (response is null || response.Arguments is null || !Enum.IsDefined(response.Operation)
            || response.Operation == MailOperationKind.None)
            throw new InvalidOperationException("A handler returned an unsupported mail operation result.");
        if (response.AdditionalResponses is not null)
        {
            foreach (var additional in response.AdditionalResponses)
                ValidateResponse(additional);
        }
    }

    private static MailApplicationException NotRequest(string detail) =>
        new(new MailApplicationFailure(MailFailureKind.MalformedBatch, detail));

}
