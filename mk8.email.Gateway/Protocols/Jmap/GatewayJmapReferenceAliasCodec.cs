using System.Text.Json.Nodes;

namespace mk8.email.Gateway.Protocols.Jmap;

// The only interpretation of the JMAP '#' creation-reference marker happens
// before an individual command crosses the durable application boundary.
internal static class GatewayJmapReferenceAliasCodec
{
    public static IReadOnlyDictionary<string, string> Collect(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        Visit(arguments, aliases, 0);
        return aliases;
    }

    private static void Visit(JsonNode? node, Dictionary<string, string> aliases, int depth)
    {
        if (depth > 64)
            throw new GatewayJmapBatchCodec.RequestException("The application arguments are too deeply nested.");
        switch (node)
        {
            case JsonObject source:
                foreach (var entry in source)
                {
                    Add(entry.Key, aliases);
                    Visit(entry.Value, aliases, depth + 1);
                }
                break;
            case JsonArray source:
                foreach (var item in source)
                    Visit(item, aliases, depth + 1);
                break;
            case JsonValue scalar when scalar.TryGetValue<string>(out var value) && value is not null:
                Add(value, aliases);
                break;
        }
    }

    private static void Add(string value, Dictionary<string, string> aliases)
    {
        if (value.Length > 0 && value[0] == '#')
            aliases.TryAdd(value, value[1..]);
    }
}
