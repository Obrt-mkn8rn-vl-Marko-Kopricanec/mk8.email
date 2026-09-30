using System.Globalization;
using System.Text.Json.Nodes;
using MimeKit;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewaySubmissionMutationCodec
{
    private static readonly Dictionary<string, MailSubmissionObservationField> Fields = new(StringComparer.Ordinal)
    {
        ["id"] = MailSubmissionObservationField.Id,
        ["identityId"] = MailSubmissionObservationField.IdentityReference,
        ["emailId"] = MailSubmissionObservationField.MessageReference,
        ["threadId"] = MailSubmissionObservationField.ThreadReference,
        ["envelope"] = MailSubmissionObservationField.Envelope,
        ["sendAt"] = MailSubmissionObservationField.SendTime,
        ["undoStatus"] = MailSubmissionObservationField.UndoState,
        ["deliveryStatus"] = MailSubmissionObservationField.Delivery,
        ["dsnBlobIds"] = MailSubmissionObservationField.DeliveryReceipts,
        ["mdnBlobIds"] = MailSubmissionObservationField.ReadReceipts,
    };

    public static MailSubmissionDraft Draft(JsonObject value) => new(
        ReadString(value["identityId"]), ReadString(value["emailId"]),
        value.Any(item => item.Key is not ("identityId" or "emailId" or "envelope")),
        value["envelope"] is null ? null : Envelope(value["envelope"]!));

    private static MailSubmissionEnvelopeDraft Envelope(JsonNode node)
    {
        if (node is not JsonObject value || value.Any(item => item.Key is not ("mailFrom" or "rcptTo"))
            || value["rcptTo"] is not JsonArray recipients)
            return new(new(string.Empty, null, MailEnvelopeAddressIssue.InvalidShape), [], true);
        return new(Address(value["mailFrom"], allowSize: true),
            recipients.Select(item => Address(item, allowSize: false)).ToArray(), false);
    }

    private static MailEnvelopeAddressDraft Address(JsonNode? node, bool allowSize)
    {
        var text = node is JsonObject address ? ReadString(address["email"]) : null;
        if (node is not JsonObject value || text is null || value.Any(item => item.Key is not ("email" or "parameters")))
            return new(text ?? string.Empty, null, MailEnvelopeAddressIssue.InvalidShape);
        if (!ValidAddress(text)) return new(text, null, MailEnvelopeAddressIssue.InvalidAddress);
        if (value["parameters"] is null) return new(text, null, MailEnvelopeAddressIssue.None);
        if (value["parameters"] is not JsonObject parameters)
            return new(text, null, MailEnvelopeAddressIssue.InvalidParameters);
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            var size = ReadString(parameter.Value);
            if (!allowSize || !parameter.Key.Equals("SIZE", StringComparison.OrdinalIgnoreCase)
                || normalized.Count != 0 || size is null || size.Length == 0 || size.Any(character => !char.IsAsciiDigit(character))
                || !long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return new(text, null, MailEnvelopeAddressIssue.InvalidParameters);
            normalized["SIZE"] = size;
        }
        return new(text, normalized, MailEnvelopeAddressIssue.None);
    }

    private static bool ValidAddress(string email)
    {
        var separator = email.LastIndexOf('@');
        return email.Length <= 254 && email.All(char.IsAscii) && separator is > 0 and <= 64
            && email.Length - separator - 1 is > 0 and <= 255 && MailboxAddress.TryParse(email, out var mailbox)
            && string.Equals(mailbox.Address, email, StringComparison.OrdinalIgnoreCase);
    }

    public static MailSubmissionPatch Patch(JsonObject patch)
    {
        if (!GatewayEmailPatchCodec.TryPaths(patch, out var paths))
            return new([], new(MailSubmissionMutationError.InvalidPatch, null, null, null, null, null));
        var assertions = new List<MailSubmissionAssertion>();
        foreach (var group in patch.Zip(paths).GroupBy(item => item.Second[0], StringComparer.Ordinal))
        {
            var field = Fields.GetValueOrDefault(group.Key, MailSubmissionObservationField.Unknown);
            var changes = group.Select(item => Change(field, item.Second.Skip(1), item.First.Value)).ToArray();
            assertions.Add(new(group.Key, field, changes));
        }
        return new(assertions.ToArray(), null);
    }

    private static MailMessageValueChange Change(MailSubmissionObservationField field, IEnumerable<string> path, JsonNode? value)
    {
        var context = field switch
        {
            MailSubmissionObservationField.Envelope => Context.Envelope,
            MailSubmissionObservationField.Delivery => Context.DeliveryMap,
            _ => Context.Opaque,
        };
        var translated = new List<string>();
        foreach (var token in path)
        {
            if (context is Context.Reply or Context.DeliveryValue or Context.DisplayValue)
                translated.Add("!Scalar");
            translated.Add(Key(token, context));
            context = Child(token, context);
        }
        return new(translated.ToArray(), ApplicationValueCodec.Encode(Canonical(value, context)));
    }

    private static JsonNode? Canonical(JsonNode? node, Context context)
    {
        if (context == Context.Reply) return Reply(node);
        if (context == Context.DeliveryValue && ReadString(node) is { } delivery)
            return JsonValue.Create(delivery switch { "queued" => "Pending", "yes" => "DeliveredLocal", "no" => "NotDelivered", "unknown" => "Unconfirmed", _ => "!" + delivery });
        if (context == Context.DisplayValue && ReadString(node) is { } display)
            return JsonValue.Create(string.Equals(display, "unknown", StringComparison.Ordinal) ? "Unreported" : "!" + display);
        if (node is JsonArray array)
            return new JsonArray(array.Select(item => Canonical(item, context)).ToArray());
        if (node is not JsonObject value) return node?.DeepClone();
        var result = new JsonObject();
        foreach (var item in value) result[Key(item.Key, context)] = Canonical(item.Value, Child(item.Key, context));
        return result;
    }

    private static JsonObject? Reply(JsonNode? node)
    {
        if (ReadString(node) is not { } reply)
            return node is null ? null : new JsonObject { ["InvalidValue"] = node.DeepClone() };
        return reply switch
        {
            "250 2.0.0 Queued for delivery" => new JsonObject { ["Kind"] = "Queued" },
            "250 2.0.0 Delivery accepted" => new JsonObject { ["Kind"] = "Accepted" },
            "250 2.0.0 Delivery status unknown" => new JsonObject { ["Kind"] = "Unspecified" },
            _ => FailureReply(reply),
        };
    }

    private static JsonObject FailureReply(string reply)
    {
        if (reply.StartsWith("550 5.0.0 ", StringComparison.Ordinal))
            return new() { ["Kind"] = "Failure", ["Detail"] = reply[10..] };
        if (reply.StartsWith("550 5.7.1 ", StringComparison.Ordinal))
            return new() { ["Kind"] = "Quarantined", ["Detail"] = reply[10..] };
        return new() { ["Kind"] = "!Invalid", ["Detail"] = reply };
    }

    private static string Key(string key, Context context) => context switch
    {
        Context.Envelope => key switch { "mailFrom" => "Sender", "rcptTo" => "Recipients", _ => "!" + key },
        Context.Address => key switch { "email" => "Address", "parameters" => "Parameters", _ => "!" + key },
        Context.Delivery => key switch { "smtpReply" => "Reply", "delivered" => "Delivery", "displayed" => "Display", _ => "!" + key },
        _ => key,
    };

    private static Context Child(string key, Context context) => context switch
    {
        Context.Envelope when key is "mailFrom" or "rcptTo" => Context.Address,
        Context.DeliveryMap => Context.Delivery,
        Context.Delivery when key is "smtpReply" => Context.Reply,
        Context.Delivery when key is "delivered" => Context.DeliveryValue,
        Context.Delivery when key is "displayed" => Context.DisplayValue,
        _ => Context.Opaque,
    };

    private static string? ReadString(JsonNode? node) => node is JsonValue value
        && value.TryGetValue<string>(out var text) ? text : null;

    private enum Context { Opaque, Envelope, Address, DeliveryMap, Delivery, Reply, DeliveryValue, DisplayValue }
}
