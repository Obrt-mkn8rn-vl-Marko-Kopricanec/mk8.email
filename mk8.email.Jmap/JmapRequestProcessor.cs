using System.Text.Json.Nodes;
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

    public JmapRequestProcessor(
        IEnumerable<IJmapMethod> methods,
        JmapAccountProfileService sessions,
        EmailDbContext database,
        EnvironmentConfig environment,
        LargeObjectTransactionEffects blobEffects,
        ILogger<JmapRequestProcessor> logger)
    {
        _methods = methods.ToDictionary(method => method.Name, StringComparer.Ordinal);
        _sessions = sessions;
        _database = database;
        _environment = environment;
        _blobEffects = blobEffects;
        _logger = logger;
    }

    public async Task<JmapApplicationBatchResult> ProcessAsync(
        JmapApplicationBatch batch,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Invocations is null)
            throw NotRequest("The application batch must contain invocations.");
        var capabilities = ValidateHeader(batch.Capabilities, batch.Invocations.Length);
        var invocations = new List<JmapApplicationInvocation>(batch.Invocations.Length);
        foreach (var invocation in batch.Invocations)
        {
            if (invocation is null || invocation.Name is null
                || invocation.Arguments is null || invocation.CorrelationId is null)
                throw NotRequest("An application invocation is incomplete.");
            invocations.Add(invocation with { Arguments = (JsonObject)invocation.Arguments.DeepClone() });
        }

        var createdIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (batch.CreatedIds is not null)
        {
            foreach (var item in batch.CreatedIds)
            {
                if (item.Value is null || !JmapId.IsValidId(item.Key) || !JmapId.IsValidId(item.Value))
                    throw NotRequest("The createdIds property contains an invalid creation id or object id.");
                createdIds.Add(item.Key, item.Value);
            }
        }
        var context = new JmapInvocationContext(user, capabilities, createdIds);
        var responses = new List<JmapApplicationInvocation>();
        var previousResponses = new List<CompletedInvocation>();

        foreach (var invocation in invocations)
        {
            JmapMethodResponse response;
            if (!TryResolveResultReferences(
                    invocation.Arguments,
                    previousResponses,
                    out var resolvedArguments,
                    out var referenceFailure))
                response = JmapMethodResponse.Error(referenceFailure);
            else if (!_methods.TryGetValue(invocation.Name, out var method)
                || !capabilities.Contains(method.Capability))
                response = JmapMethodResponse.Error("unknownMethod");
            else
                response = await InvokeAtomicallyAsync(
                    method, context, resolvedArguments, cancellationToken).ConfigureAwait(false);

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
                previousResponses.Add(new CompletedInvocation(invocation.CorrelationId, completed.Name, arguments));
            }
        }

        var profile = await _sessions.GetProfileAsync(user, cancellationToken).ConfigureAwait(false);
        return new JmapApplicationBatchResult(
            responses.ToArray(), profile, batch.CreatedIds is null ? null : createdIds);
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
        CancellationToken cancellationToken)
    {
        var createdIds = context.CreatedIds.ToDictionary(item => item.Key, item => item.Value);
        var postCommitMarker = context.MarkPostCommitActions();
        var blobEffectMarker = _blobEffects.Mark();
        IDbContextTransaction? transaction = null;
        var commitAttempted = false;
        try
        {
            if (_database.Database.IsRelational())
                transaction = await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var response = await method.InvokeAsync(context, arguments, cancellationToken).ConfigureAwait(false);
            if (MustRollBack(response))
            {
                await RollBackAsync(transaction).ConfigureAwait(false);
                await _blobEffects.RollbackAsync(blobEffectMarker).ConfigureAwait(false);
                RestoreInvocationState(context, createdIds, postCommitMarker);
                return response;
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
            throw;
        }
        catch (Exception exception)
        {
            await RollBackAsync(transaction).ConfigureAwait(false);
            await CompleteBlobRollbackAsync(blobEffectMarker, commitAttempted).ConfigureAwait(false);
            RestoreInvocationState(context, createdIds, postCommitMarker);
            _logger.LogError(exception, "JMAP method {MethodName} failed", method.Name);
            return JmapMethodResponse.Error("serverFail");
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
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

    private static bool TryResolveResultReferences(
        JsonObject arguments,
        IReadOnlyList<CompletedInvocation> previousResponses,
        out JsonObject resolvedArguments,
        out string failure)
    {
        resolvedArguments = (JsonObject)arguments.DeepClone();
        failure = "invalidResultReference";
        foreach (var property in arguments.ToList())
        {
            if (!property.Key.StartsWith('#'))
                continue;

            var targetName = property.Key[1..];
            if (targetName.Length == 0 || arguments.ContainsKey(targetName))
            {
                failure = "invalidArguments";
                return false;
            }
            if (property.Value is not JsonObject reference
                || !TryGetRequiredString(reference, "resultOf", out var resultOf)
                || !TryGetRequiredString(reference, "name", out var responseName)
                || !TryGetRequiredString(reference, "path", out var path)
                || reference.Any(item => item.Key is not ("resultOf" or "name" or "path")))
            {
                return false;
            }

            var response = previousResponses.FirstOrDefault(item => item.CallId == resultOf);
            if (response is null
                || response.Name != responseName
                || !TryApplyJsonPointer(response.Arguments, path, out var referencedValue))
            {
                return false;
            }

            resolvedArguments.Remove(property.Key);
            resolvedArguments[targetName] = referencedValue;
        }

        failure = string.Empty;
        return true;
    }

    private static bool TryApplyJsonPointer(JsonNode root, string pointer, out JsonNode? value)
    {
        value = null;
        if (pointer.Length == 0)
        {
            value = root.DeepClone();
            return true;
        }
        if (pointer[0] != '/')
            return false;

        var tokens = pointer[1..].Split('/');
        var decoded = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!TryDecodePointerToken(token, out var decodedToken))
                return false;
            decoded.Add(decodedToken);
        }

        return TryApplyPointerTokens(root, decoded, 0, out value);
    }

    private static bool TryApplyPointerTokens(
        JsonNode? current,
        IReadOnlyList<string> tokens,
        int index,
        out JsonNode? value)
    {
        value = null;
        if (index == tokens.Count)
        {
            value = current?.DeepClone();
            return true;
        }

        var token = tokens[index];
        if (current is JsonArray array && token == "*")
        {
            var mapped = new JsonArray();
            foreach (var item in array)
            {
                if (!TryApplyPointerTokens(item, tokens, index + 1, out var mappedItem))
                    return false;
                if (mappedItem is JsonArray mappedArray)
                {
                    foreach (var nested in mappedArray)
                        mapped.Add(nested?.DeepClone());
                }
                else
                {
                    mapped.Add(mappedItem);
                }
            }
            value = mapped;
            return true;
        }

        if (current is JsonObject jsonObject
            && jsonObject.TryGetPropertyValue(token, out var propertyValue))
        {
            return TryApplyPointerTokens(propertyValue, tokens, index + 1, out value);
        }

        if (current is JsonArray jsonArray
            && int.TryParse(token, System.Globalization.NumberStyles.None, null, out var arrayIndex)
            && (token == "0" || token.Length > 0 && token[0] != '0')
            && arrayIndex >= 0
            && arrayIndex < jsonArray.Count)
        {
            return TryApplyPointerTokens(jsonArray[arrayIndex], tokens, index + 1, out value);
        }

        return false;
    }

    private static bool TryDecodePointerToken(string token, out string decoded)
    {
        var builder = new System.Text.StringBuilder(token.Length);
        for (var index = 0; index < token.Length; index++)
        {
            var character = token[index];
            if (character != '~')
            {
                builder.Append(character);
                continue;
            }

            if (++index >= token.Length)
            {
                decoded = string.Empty;
                return false;
            }
            builder.Append(token[index] switch
            {
                '0' => '~',
                '1' => '/',
                _ => '\0',
            });
            if (builder[^1] == '\0')
            {
                decoded = string.Empty;
                return false;
            }
        }

        decoded = builder.ToString();
        return true;
    }

    private static bool TryGetRequiredString(JsonObject value, string name, out string result)
    {
        result = string.Empty;
        if (value[name] is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var parsed)
            || parsed is null)
        {
            return false;
        }

        result = parsed;
        return true;
    }

    private static JmapRequestException NotRequest(string detail) =>
        new(
            "urn:ietf:params:jmap:error:notRequest",
            "Invalid JMAP request",
            detail);

    private sealed record CompletedInvocation(string CallId, string Name, JsonObject Arguments);
}
