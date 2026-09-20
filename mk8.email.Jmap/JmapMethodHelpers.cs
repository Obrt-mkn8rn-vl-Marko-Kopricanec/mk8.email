using System.Text.Json.Nodes;

namespace mk8.email.Jmap;

internal static class JmapMethodHelpers
{
    public const long MaximumInt = 9_007_199_254_740_991;
    public const long MinimumInt = -MaximumInt;

    public static bool HasOnlyProperties(JsonObject value, params string[] propertyNames)
    {
        var allowed = propertyNames.ToHashSet(StringComparer.Ordinal);
        return value.All(property => allowed.Contains(property.Key));
    }

    public static bool TryGetRequiredString(
        JsonObject arguments,
        string name,
        out string value)
    {
        value = string.Empty;
        return arguments[name] is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out value!)
            && value is not null;
    }

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
            && jsonValue.TryGetValue<long>(out value)
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
            || !jsonValue.TryGetValue<long>(out var parsed)
            || parsed is < 0 or > MaximumInt)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    public static int ClampToServerLimit(long? requested, int serverMaximum) =>
        checked((int)Math.Min(requested ?? serverMaximum, serverMaximum));

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
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var parsed)
                || parsed is null
                || !unique.Add(parsed))
            {
                return false;
            }
            result.Add(parsed);
        }

        values = result;
        return true;
    }

    public static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    public static JsonObject SetError(
        string type,
        string? description = null,
        IEnumerable<string>? properties = null)
    {
        var error = new JsonObject { ["type"] = type };
        if (!string.IsNullOrWhiteSpace(description))
            error["description"] = description;
        if (properties is not null)
            error["properties"] = ToJsonArray(properties);
        return error;
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

    public static bool TryApplyPatchAllowingUnchangedProperties(
        JsonObject source,
        JsonObject patch,
        IReadOnlySet<string> mutableProperties,
        out JsonObject result,
        out IReadOnlyList<string> invalidProperties)
    {
        invalidProperties = [];
        if (!TryApplyPatch(source, patch, out result))
            return false;

        var invalid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in patch.KeysForPatch())
        {
            if (mutableProperties.Contains(property))
                continue;
            if (!source.TryGetPropertyValue(property, out var original)
                || !result.TryGetPropertyValue(property, out var revised)
                || !JsonNode.DeepEquals(original, revised))
            {
                invalid.Add(property);
            }
        }
        invalidProperties = invalid.Order(StringComparer.Ordinal).ToArray();
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
        path = result;
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
