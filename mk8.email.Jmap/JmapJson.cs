using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace mk8.email.Jmap;

internal static class JmapJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    internal static bool ContainsOnlyUnicodeScalars(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out _, out var consumed);
            if (status != OperationStatus.Done)
                return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    private static bool ContainsOnlyIJsonCharacters(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done || IsUnicodeNoncharacter(rune.Value))
                return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    internal static JsonObject SanitizeResponse(JsonObject value) =>
        (JsonObject)SanitizeResponseNode(value)!;

    private static JsonNode? SanitizeResponseNode(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonObject source:
                var result = new JsonObject();
                foreach (var property in source)
                {
                    var name = SanitizeIJsonString(property.Key);
                    if (result.ContainsKey(name))
                    {
                        var suffix = 2;
                        var candidate = $"{name}~{suffix}";
                        while (result.ContainsKey(candidate))
                            candidate = $"{name}~{++suffix}";
                        name = candidate;
                    }
                    result[name] = SanitizeResponseNode(property.Value);
                }
                return result;
            case JsonArray source:
                var array = new JsonArray();
                foreach (var item in source)
                    array.Add(SanitizeResponseNode(item));
                return array;
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                return JsonValue.Create(SanitizeIJsonString(text));
            default:
                return value.DeepClone();
        }
    }

    private static string SanitizeIJsonString(string value)
    {
        if (ContainsOnlyIJsonCharacters(value))
            return value;

        var result = new StringBuilder(value.Length);
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                result.Append('\ufffd');
                remaining = remaining[1..];
                continue;
            }

            result.Append(IsUnicodeNoncharacter(rune.Value) ? "\ufffd" : rune.ToString());
            remaining = remaining[consumed..];
        }
        return result.ToString();
    }

    private static bool IsUnicodeNoncharacter(int value) =>
        value is >= 0xFDD0 and <= 0xFDEF
        || (value & 0xFFFE) == 0xFFFE;

}
