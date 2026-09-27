using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayJmapArgumentBindingCodec
{
    public static JmapApplicationCall ParseCall(string name, JsonObject arguments, string correlationId)
    {
        var input = new JsonObject();
        var bindings = new List<ApplicationArgumentBinding>();
        foreach (var property in arguments)
        {
            if (property.Key.StartsWith('#'))
                bindings.Add(ParseBinding(property.Key[1..], property.Value, arguments));
            else
                input.Add(property.Key, property.Value?.DeepClone());
        }
        return new JmapApplicationCall(GatewayJmapOperationCodec.DecodeCall(name), input, correlationId, bindings.Count == 0 ? null : bindings.ToArray());
    }

    private static ApplicationArgumentBinding ParseBinding(string target, JsonNode? value, JsonObject arguments)
    {
        if (target.Length == 0 || arguments.ContainsKey(target))
            return Failed(target, ApplicationBindingFailure.InvalidTarget);
        if (value is not JsonObject reference
            || !TryGetRequiredString(reference, "resultOf", out var resultOf)
            || !TryGetRequiredString(reference, "name", out var responseName)
            || !TryGetRequiredString(reference, "path", out var path)
            || reference.Any(item => item.Key is not ("resultOf" or "name" or "path"))
            || !TryParsePath(path, out var segments))
            return Failed(target, ApplicationBindingFailure.InvalidSource);
        return new ApplicationArgumentBinding(target, resultOf, GatewayJmapOperationCodec.DecodeReference(responseName), segments);
    }

    private static ApplicationArgumentBinding Failed(string target, ApplicationBindingFailure failure) =>
        new(target, string.Empty, MailOperationKind.None, [], failure);

    private static bool TryParsePath(string pointer, out ApplicationValuePathSegment[] segments)
    {
        segments = [];
        if (pointer.Length == 0)
            return true;
        if (pointer[0] != '/')
            return false;
        var tokens = pointer[1..].Split('/');
        var result = new ApplicationValuePathSegment[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!TryDecodeToken(tokens[index], out var token))
                return false;
            int? arrayIndex = null;
            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                && (string.Equals(token, "0", StringComparison.Ordinal) || token.Length > 0 && token[0] != '0'))
                arrayIndex = parsed;
            result[index] = new ApplicationValuePathSegment(token, arrayIndex, string.Equals(token, "*", StringComparison.Ordinal));
        }
        segments = result;
        return true;
    }

    private static bool TryDecodeToken(string token, out string decoded)
    {
        var builder = new StringBuilder(token.Length);
        for (var index = 0; index < token.Length; index++)
        {
            var character = token[index];
            if (character != '~')
            {
                builder.Append(character);
                continue;
            }
            if (++index >= token.Length || token[index] is not ('0' or '1'))
            {
                decoded = string.Empty;
                return false;
            }
            builder.Append(token[index] == '0' ? '~' : '/');
        }
        decoded = builder.ToString();
        return true;
    }

    private static bool TryGetRequiredString(JsonObject value, string name, out string result)
    {
        result = string.Empty;
        if (value[name] is not JsonValue scalar || !scalar.TryGetValue<string>(out var parsed) || parsed is null)
            return false;
        result = parsed;
        return true;
    }
}
