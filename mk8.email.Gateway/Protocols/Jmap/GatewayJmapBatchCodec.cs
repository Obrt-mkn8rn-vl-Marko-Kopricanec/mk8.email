using System.Text.Json.Nodes;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayJmapBatchCodec
{
    public static JmapApplicationBatch Parse(JsonNode? node, out JmapBatchPreflight? preflight)
    {
        preflight = null;
        if (node is not JsonObject request
            || request["using"] is not JsonArray usingNode
            || request["methodCalls"] is not JsonArray calls)
            throw NotRequest("The JSON document is not a valid JMAP Request object.");

        var capabilities = new string[usingNode.Count];
        for (var index = 0; index < usingNode.Count; index++)
        {
            if (usingNode[index] is not JsonValue value
                || !value.TryGetValue<string>(out var capability)
                || capability is null)
                throw NotRequest("The using property must contain capability strings.");
            capabilities[index] = capability;
        }
        // Worker still checks authorization, capacity and supported capabilities
        // before Gateway reveals a later malformed-invocation/createdIds error.
        preflight = new JmapBatchPreflight(capabilities, calls.Count);

        var invocations = new JmapApplicationCall[calls.Count];
        for (var index = 0; index < calls.Count; index++)
        {
            if (calls[index] is not JsonArray { Count: 3 } invocation
                || invocation[0] is not JsonValue methodValue
                || !methodValue.TryGetValue<string>(out var name)
                || name is null
                || invocation[1] is not JsonObject arguments
                || invocation[2] is not JsonValue correlationValue
                || !correlationValue.TryGetValue<string>(out var correlationId)
                || correlationId is null)
                throw NotRequest("A methodCalls entry is not a valid Invocation object.");
            invocations[index] = GatewayJmapArgumentBindingCodec.ParseCall(name, arguments, correlationId);
        }

        Dictionary<string, string>? createdIds = null;
        if (request.TryGetPropertyValue("createdIds", out var createdNode))
        {
            if (createdNode is not JsonObject created)
                throw NotRequest("The createdIds property must be an object when present.");
            createdIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in created)
            {
                if (!IsId(entry.Key)
                    || entry.Value is not JsonValue value
                    || !value.TryGetValue<string>(out var id)
                    || id is null
                    || !IsId(id))
                    throw NotRequest("The createdIds property contains an invalid creation id or object id.");
                createdIds.Add(entry.Key, id);
            }
        }
        return new JmapApplicationBatch(capabilities, invocations, createdIds);
    }

    public static JsonObject Render(JmapApplicationBatchResult batch, EnvironmentConfig environment)
    {
        var responses = new JsonArray();
        foreach (var invocation in batch.Invocations)
            responses.Add(new JsonArray(GatewayJmapOperationCodec.Render(invocation.Operation), invocation.Arguments.DeepClone(), invocation.CorrelationId));
        var result = new JsonObject
        {
            ["methodResponses"] = responses,
            ["sessionState"] = GatewayJmapProfileCodec.Render(batch.Profile, environment)["state"]!.DeepClone(),
        };
        if (batch.CreatedIds is not null)
        {
            var created = new JsonObject();
            foreach (var entry in batch.CreatedIds)
                created[entry.Key] = entry.Value;
            result["createdIds"] = created;
        }
        return result;
    }

    private static bool IsId(string value)
    {
        if (value.Length is < 1 or > 255)
            return false;
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
                return false;
        }
        return true;
    }

    private static RequestException NotRequest(string detail) => new(
        "urn:ietf:params:jmap:error:notRequest", "Invalid JMAP request", detail);

    internal sealed class RequestException : Exception
    {
        public RequestException() : this("Invalid JMAP request")
        {
        }

        public RequestException(string message) : base(message)
        {
            Problem = new JmapApplicationProblem("urn:ietf:params:jmap:error:notRequest", message);
        }

        public RequestException(string message, Exception innerException) : base(message, innerException)
        {
            Problem = new JmapApplicationProblem("urn:ietf:params:jmap:error:notRequest", message);
        }

        public RequestException(string type, string title, string? detail = null, string? limit = null)
            : base(detail ?? title)
        {
            Problem = new JmapApplicationProblem(type, title, detail, limit);
        }

        public JmapApplicationProblem Problem { get; }
    }
}
