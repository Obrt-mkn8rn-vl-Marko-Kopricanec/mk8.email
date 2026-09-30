using System.Text.Json;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Infrastructure.Models;

// The optional database cache retains its historical encoding for binary rollback.
// Application uses typed envelopes; this codec is not a request parser or response renderer.
public static class MailSubmissionEnvelopeCache
{
    public static string Encode(MailSubmissionEnvelope value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new JsonObject
        {
            ["mailFrom"] = EncodeAddress(value.Sender),
            ["rcptTo"] = new JsonArray(value.Recipients.Select(EncodeAddress).Cast<JsonNode?>().ToArray()),
        }.ToJsonString();
    }

    private static JsonObject EncodeAddress(MailEnvelopeAddress value)
    {
        JsonObject? parameters = null;
        if (value.Parameters is not null)
        {
            parameters = new();
            foreach (var item in value.Parameters) parameters[item.Key] = item.Value;
        }
        return new() { ["email"] = value.Address, ["parameters"] = parameters };
    }

    public static MailSubmissionEnvelope Read(JmapEmailSubmissionDB row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.EnvelopeJson is not null)
        {
            try
            {
                var node = JsonNode.Parse(row.EnvelopeJson);
                if (node is JsonObject legacy && legacy["rcptTo"] is JsonArray recipients
                    && DecodeAddress(legacy["mailFrom"]) is { } sender)
                {
                    var addresses = recipients.Select(DecodeAddress).ToArray();
                    if (addresses.All(address => address is not null)) return new(sender, addresses.Select(address => address!).ToArray());
                }
            }
            catch (JsonException)
            {
                // Damaged optional caches fall back to the indexed envelope.
            }
        }
        return new(new(row.EnvelopeSender, null), row.EnvelopeRecipients.Select(address => new MailEnvelopeAddress(address, null)).ToArray());
    }

    private static MailEnvelopeAddress? DecodeAddress(JsonNode? node)
    {
        if (node is not JsonObject value || value["email"] is not JsonValue email
            || !email.TryGetValue<string>(out var address) || address is null) return null;
        if (value["parameters"] is null) return new(address, null);
        if (value["parameters"] is not JsonObject parameters) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in parameters)
        {
            if (item.Value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || text is null) return null;
            values[item.Key] = text;
        }
        return new(address, values);
    }
}
