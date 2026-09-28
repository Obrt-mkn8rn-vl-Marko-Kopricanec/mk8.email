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
        MaxDepth = 256,
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

    internal Task<JmapApplicationProfile> GetProfileAsync(AuthenticatedMailUser user, CancellationToken token = default) =>
        _sessions.GetProfileAsync(user, token);

    public async Task<MailOperationResult> ExecuteAsync(
        MailOperationCommand command,
        AuthenticatedMailUser user,
        Guid? operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(user);
        if (command.Features is null || command.Arguments is null || !Enum.IsDefined(command.Operation)
            || command.Operation is MailOperationKind.Failure or MailOperationKind.Echo)
            throw NotRequest("The mail operation command is incomplete.");
        var features = ValidateHeader(command.Features.ToArray(), 1);
        var createdIds = CloneCreatedIds(command.KnownEntities);
        var context = new JmapInvocationContext(user, features, createdIds)
        {
            ReferenceAliases = CloneReferenceAliases(command.ReferenceAliases),
        };
        var relational = _database.Database.IsRelational();
        if (relational && (operationId is null || operationId == Guid.Empty || _receipts is null))
            throw new InvalidOperationException("A durable operation identity and receipt store are required.");
        var inputHash = relational
            ? Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, ReceiptJsonOptions)))
            : string.Empty;
        var receiptKey = relational
            ? new ApplicationReceiptKey(operationId!.Value, 0, user.Id, "mail.operation", inputHash)
            : null;
        var response = !_methods.TryGetValue(command.Operation, out var method) || !features.Contains(method.Feature)
            ? EncodeResponse(JmapMethodResponse.Error("unknownMethod"))
            : await InvokeAtomicallyAsync(method, context, (JsonObject)command.Arguments.DeepClone(),
                receiptKey, cancellationToken).ConfigureAwait(false);
        var profile = await _sessions.GetProfileAsync(user, cancellationToken).ConfigureAwait(false);
        return new MailOperationResult(response, createdIds, profile);
    }

    private static Dictionary<string, string> CloneCreatedIds(IReadOnlyDictionary<string, string>? values)
    {
        var createdIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values is not null)
        {
            foreach (var item in values)
            {
                if (item.Value is null || !JmapId.IsValidId(item.Key) || !JmapId.IsValidId(item.Value))
                    throw NotRequest("The known entity map contains an invalid identifier.");
                createdIds.Add(item.Key, item.Value);
            }
        }
        return createdIds;
    }

    private static Dictionary<string, string> CloneReferenceAliases(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
            throw NotRequest("The reference alias map is required.");
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            if (item.Key is null || item.Value is null)
                throw NotRequest("The reference alias map is incomplete.");
            aliases.Add(item.Key, item.Value);
        }
        return aliases;
    }

    internal void ValidatePlan(MailAdmissionPlan? plan)
    {
        if (plan is not null)
            _ = ValidateHeader(plan.Features, plan.OperationCount);
    }

    private HashSet<MailFeature> ValidateHeader(IReadOnlyList<MailFeature>? values, int invocationCount)
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

    private async Task<MailOperationResponse> InvokeAtomicallyAsync(
        IJmapMethod method,
        JmapInvocationContext context,
        JsonObject arguments,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var createdIds = context.CreatedIds.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
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
                    ValidateEncodedResponse(replay.Response);
                    context.CreatedIds.Clear();
                    foreach (var item in replay.CreatedIds)
                        context.CreatedIds[item.Key] = item.Value;
                    return replay.Response;
                }
            }

            var response = await method.InvokeAsync(context, arguments, cancellationToken).ConfigureAwait(false);
            ValidateResponse(response);
            var encoded = EncodeResponse(response);
            if (MustRollBack(response))
            {
                await RollBackAsync(transaction).ConfigureAwait(false);
                await _blobEffects.RollbackAsync(blobEffectMarker).ConfigureAwait(false);
                RestoreInvocationState(context, createdIds, postCommitMarker);
                context.DiscardPresentationEffects(presentationMarker);
                return encoded;
            }

            if (receiptKey is not null)
            {
                var result = JsonSerializer.SerializeToUtf8Bytes(new JmapReplayState(
                    encoded, context.CreatedIds.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)), ReceiptJsonOptions);
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
            return encoded;
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
            return EncodeResponse(JmapMethodResponse.Error("serverFail"));
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
            context.DiscardPresentationEffects(presentationMarker);
        }
    }

    private static MailOperationResponse EncodeResponse(JmapMethodResponse response) => new(
        response.Operation, ApplicationValueCodec.Encode(response.Arguments),
        response.AdditionalResponses?.Select(EncodeResponse).ToArray());

    private static void ValidateEncodedResponse(MailOperationResponse response)
    {
        if (response is null || !Enum.IsDefined(response.Operation) || response.Operation is MailOperationKind.None or MailOperationKind.Echo
            || response.Data is null || ApplicationValueCodec.Decode(response.Data) is not JsonObject)
            throw new InvalidOperationException("A mail operation receipt contains an invalid result.");
        if (response.AdditionalResults is not null)
        {
            foreach (var additional in response.AdditionalResults)
                ValidateEncodedResponse(additional);
        }
    }

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

    private static MailOperationKind ValidRegisteredOperation(MailOperationKind operation, MailFeature feature) =>
        Enum.IsDefined(operation) && operation is not (MailOperationKind.None or MailOperationKind.Failure or MailOperationKind.Echo)
            && Enum.IsDefined(feature) && feature != MailFeature.Unsupported
            ? operation : throw new ArgumentException("A handler must register a supported mail operation.", nameof(operation));

    private static void ValidateResponse(JmapMethodResponse response)
    {
        if (response is null || response.Arguments is null || !Enum.IsDefined(response.Operation)
            || response.Operation is MailOperationKind.None or MailOperationKind.Echo)
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
