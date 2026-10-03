using System.Text.Json.Nodes;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayDocumentValues
{
    public const long MaximumInt = 9_007_199_254_740_991;
    public const long MinimumInt = -MaximumInt;

    public static bool TryGetOptionalString(
        JsonObject arguments,
        string name,
        out string? value,
        bool allowNull = true)
    {
        value = null;
        if (!arguments.TryGetPropertyValue(name, out var node))
            return true;
        if (node is null)
            return allowNull;

        return node is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out value);
    }

    public static bool TryGetOptionalBoolean(
        JsonObject arguments,
        string name,
        bool defaultValue,
        out bool value)
    {
        value = defaultValue;
        if (!arguments.TryGetPropertyValue(name, out var node))
            return true;
        if (node is null)
            return false;

        return node is JsonValue jsonValue
            && jsonValue.TryGetValue<bool>(out value);
    }

    public static bool TryGetOptionalInt(
        JsonObject arguments,
        string name,
        long defaultValue,
        out long value)
    {
        value = defaultValue;
        if (!arguments.TryGetPropertyValue(name, out var node))
            return true;
        if (node is null)
            return false;

        return node is JsonValue jsonValue
            && TryGetInteger(jsonValue, out value)
            && value is >= MinimumInt and <= MaximumInt;
    }

    public static bool TryGetOptionalUnsignedInt(
        JsonObject arguments,
        string name,
        out long? value,
        bool allowNull = true)
    {
        value = null;
        if (!arguments.TryGetPropertyValue(name, out var node))
            return true;
        if (node is null)
            return allowNull;
        if (node is not JsonValue jsonValue
            || !TryGetInteger(jsonValue, out var parsed)
            || parsed is < 0 or > MaximumInt)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetInteger(JsonValue value, out long parsed)
    {
        if (value.TryGetValue<long>(out parsed))
            return true;
        if (value.TryGetValue<int>(out var signed))
        {
            parsed = signed;
            return true;
        }
        if (value.TryGetValue<uint>(out var unsigned))
        {
            parsed = unsigned;
            return true;
        }
        if (value.TryGetValue<ulong>(out var wideUnsigned) && wideUnsigned <= long.MaxValue)
        {
            parsed = checked((long)wideUnsigned);
            return true;
        }
        parsed = 0;
        return false;
    }

    public static bool TryGetStringArray(
        JsonObject arguments,
        string name,
        bool nullable,
        out IReadOnlyList<string>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue(name, out var node))
            return true;
        if (node is null)
            return nullable;
        if (node is not JsonArray array)
            return false;

        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var parsed)
                || parsed is null)
            {
                return false;
            }
            result.Add(parsed);
        }

        values = result.ToArray();
        return true;
    }

    public static bool TryApplyPatch(
        JsonObject source,
        JsonObject patch,
        out JsonObject result)
    {
        result = (JsonObject)source.DeepClone();
        var parsedPaths = new List<IReadOnlyList<string>>(patch.Count);
        foreach (var item in patch)
        {
            if (!TryParsePatchPath(item.Key, out var path))
                return false;
            parsedPaths.Add(path);
        }

        for (var left = 0; left < parsedPaths.Count; left++)
        {
            for (var right = left + 1; right < parsedPaths.Count; right++)
            {
                if (IsPrefix(parsedPaths[left], parsedPaths[right])
                    || IsPrefix(parsedPaths[right], parsedPaths[left]))
                {
                    return false;
                }
            }
        }

        var index = 0;
        foreach (var item in patch)
        {
            var path = parsedPaths[index++];
            JsonObject parent = result;
            for (var partIndex = 0; partIndex < path.Count - 1; partIndex++)
            {
                if (!parent.TryGetPropertyValue(path[partIndex], out var child)
                    || child is not JsonObject childObject)
                {
                    return false;
                }
                parent = childObject;
            }

            var propertyName = path[^1];
            if (item.Value is null)
                parent.Remove(propertyName);
            else
                parent[propertyName] = item.Value.DeepClone();
        }

        return true;
    }

    private static bool TryParsePatchPath(string value, out IReadOnlyList<string> path)
    {
        path = [];
        if (value.Length == 0 || value[0] == '/' || value[^1] == '/')
            return false;

        var result = new List<string>();
        foreach (var token in value.Split('/'))
        {
            if (!TryDecodePointerToken(token, out var decoded) || decoded.Length == 0)
                return false;
            result.Add(decoded);
        }
        path = result.ToArray();
        return true;
    }

    private static bool TryDecodePointerToken(string token, out string decoded)
    {
        var builder = new System.Text.StringBuilder(token.Length);
        for (var index = 0; index < token.Length; index++)
        {
            if (token[index] != '~')
            {
                builder.Append(token[index]);
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

    private static bool IsPrefix(
        IReadOnlyList<string> possiblePrefix,
        IReadOnlyList<string> value)
    {
        if (possiblePrefix.Count >= value.Count)
            return false;
        for (var index = 0; index < possiblePrefix.Count; index++)
        {
            if (!string.Equals(possiblePrefix[index], value[index], StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
