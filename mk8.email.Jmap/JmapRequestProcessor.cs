using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Jmap;

public sealed class JmapRequestProcessor
{
    private readonly IReadOnlyDictionary<string, IJmapMethod> _methods;
    private readonly JmapSessionService _sessions;
    private readonly EmailDbContext _database;
    private readonly EnvironmentConfig _environment;
    private readonly ILogger<JmapRequestProcessor> _logger;

    public JmapRequestProcessor(
        IEnumerable<IJmapMethod> methods,
        JmapSessionService sessions,
        EmailDbContext database,
        EnvironmentConfig environment,
        ILogger<JmapRequestProcessor> logger)
    {
        _methods = methods.ToDictionary(method => method.Name, StringComparer.Ordinal);
        _sessions = sessions;
        _database = database;
        _environment = environment;
        _logger = logger;
    }

    public async Task<JsonObject> ProcessAsync(
        JsonNode? requestNode,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken = default)
    {
        if (requestNode is not JsonObject request
            || request["using"] is not JsonArray usingNode
            || request["methodCalls"] is not JsonArray methodCalls)
        {
            throw NotRequest("The JSON document is not a valid JMAP Request object.");
        }

        var capabilities = ParseCapabilities(usingNode);
        var supportedCapabilities = _methods.Values
            .Select(method => method.Capability)
            .Append(JmapConstants.CoreCapability)
            .ToHashSet(StringComparer.Ordinal);
        var unknownCapability = capabilities.FirstOrDefault(capability => !supportedCapabilities.Contains(capability));
        if (unknownCapability is not null)
        {
            throw new JmapRequestException(
                "urn:ietf:params:jmap:error:unknownCapability",
                StatusCodes.Status400BadRequest,
                "Unknown capability",
                $"The request uses an unsupported capability: {unknownCapability}");
        }
        if (!capabilities.Contains(JmapConstants.CoreCapability))
            throw NotRequest("The using property must include the JMAP core capability.");

        if (methodCalls.Count > _environment.Jmap.MaxCallsInRequest)
        {
            throw new JmapRequestException(
                "urn:ietf:params:jmap:error:limit",
                StatusCodes.Status400BadRequest,
                "Request limit exceeded",
                "The request contains too many method calls.",
                "maxCallsInRequest");
        }

        var methodResponses = new JsonArray();
        var previousResponses = new List<CompletedInvocation>();
        var hasCreatedIds = request.TryGetPropertyValue("createdIds", out var createdIdsNode);
        if (hasCreatedIds && createdIdsNode is null)
            throw NotRequest("The createdIds property must be an object when present.");
        var createdIds = ParseCreatedIds(createdIdsNode);
        var context = new JmapInvocationContext(user, capabilities, createdIds);

        foreach (var methodCallNode in methodCalls)
        {
            if (!TryParseInvocation(methodCallNode, out var methodName, out var arguments, out var callId))
                throw NotRequest("A methodCalls entry is not a valid Invocation object.");

            JmapMethodResponse response;
            if (!TryResolveResultReferences(arguments, previousResponses, out var resolvedArguments, out var referenceFailure))
            {
                response = JmapMethodResponse.Error(referenceFailure);
            }
            else if (!_methods.TryGetValue(methodName, out var method)
                || !capabilities.Contains(method.Capability))
            {
                response = JmapMethodResponse.Error("unknownMethod");
            }
            else
            {
                response = await InvokeAtomicallyAsync(
                    method,
                    context,
                    resolvedArguments,
                    cancellationToken);
            }

            AddResponse(response);
            if (response.AdditionalResponses is not null)
            {
                foreach (var additional in response.AdditionalResponses)
                    AddResponse(additional);
            }

            void AddResponse(JmapMethodResponse completed)
            {
                var arguments = JmapJson.SanitizeResponse(completed.Arguments);
                var invocation = new JsonArray(
                    completed.Name,
                    arguments.DeepClone(),
                    callId);
                methodResponses.Add(invocation);
                previousResponses.Add(new CompletedInvocation(
                    callId,
                    completed.Name,
                    arguments));
            }
        }

        var session = await _sessions.BuildAsync(user, cancellationToken);
        var result = new JsonObject
        {
            ["methodResponses"] = methodResponses,
            ["sessionState"] = session.State,
        };
        if (hasCreatedIds)
        {
            var created = new JsonObject();
            foreach (var item in createdIds)
                created[item.Key] = item.Value;
            result["createdIds"] = created;
        }

        return result;
    }

    private async Task<JmapMethodResponse> InvokeAtomicallyAsync(
        IJmapMethod method,
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var createdIds = context.CreatedIds.ToDictionary(item => item.Key, item => item.Value);
        var postCommitMarker = context.MarkPostCommitActions();
        IDbContextTransaction? transaction = null;
        try
        {
            if (_database.Database.IsRelational())
                transaction = await _database.Database.BeginTransactionAsync(cancellationToken);

            var response = await method.InvokeAsync(context, arguments, cancellationToken);
            if (MustRollBack(response))
            {
                await RollBackAsync(transaction);
                RestoreInvocationState(context, createdIds, postCommitMarker);
                return response;
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            var actions = context.TakePostCommitActions(postCommitMarker);
            foreach (var action in actions)
            {
                try
                {
                    // The database commit makes these actions durable work.
                    // A client disconnect must not prevent push verification
                    // (or any future committed external effect) from running.
                    await action(CancellationToken.None);
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
            await RollBackAsync(transaction);
            RestoreInvocationState(context, createdIds, postCommitMarker);
            throw;
        }
        catch (Exception exception)
        {
            await RollBackAsync(transaction);
            RestoreInvocationState(context, createdIds, postCommitMarker);
            _logger.LogError(exception, "JMAP method {MethodName} failed", method.Name);
            return JmapMethodResponse.Error("serverFail");
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
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
            await transaction.RollbackAsync(CancellationToken.None);
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

    private static HashSet<string> ParseCapabilities(JsonArray values)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is not JsonValue jsonValue
                || !jsonValue.TryGetValue<string>(out var capability)
                || capability is null)
            {
                throw NotRequest("The using property must contain capability strings.");
            }
            result.Add(capability);
        }
        return result;
    }

    private static Dictionary<string, string> ParseCreatedIds(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is null)
            return result;
        if (node is not JsonObject values)
            throw NotRequest("The createdIds property must be an object or null.");
        foreach (var item in values)
        {
            if (!JmapId.IsValidId(item.Key)
                || item.Value is not JsonValue value
                || !value.TryGetValue<string>(out var id)
                || id is null
                || !JmapId.IsValidId(id))
                throw NotRequest("The createdIds property contains an invalid creation id or object id.");
            result.Add(item.Key, id);
        }
        return result;
    }

    private static bool TryParseInvocation(
        JsonNode? node,
        out string methodName,
        out JsonObject arguments,
        out string callId)
    {
        methodName = string.Empty;
        arguments = null!;
        callId = string.Empty;
        if (node is not JsonArray { Count: 3 } invocation
            || invocation[0] is not JsonValue methodValue
            || !methodValue.TryGetValue<string>(out var parsedMethodName)
                || invocation[1] is not JsonObject argumentValue
            || invocation[2] is not JsonValue callValue
            || !callValue.TryGetValue<string>(out var parsedCallId)
            || parsedCallId is null)
        {
            return false;
        }

        methodName = parsedMethodName;
        callId = parsedCallId;
        arguments = (JsonObject)argumentValue.DeepClone();
        return true;
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
            StatusCodes.Status400BadRequest,
            "Invalid JMAP request",
            detail);

    private sealed record CompletedInvocation(string CallId, string Name, JsonObject Arguments);
}
