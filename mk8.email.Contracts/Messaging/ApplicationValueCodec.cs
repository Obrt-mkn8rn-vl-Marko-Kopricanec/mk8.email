using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace mk8.email.Contracts.Messaging;

public static class ApplicationValueCodec
{
    public static ApplicationValue Encode(JsonNode? value) => EncodeNode(value, 0);

    public static JsonNode? Decode(ApplicationValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return DecodeNode(value, 0);
    }

    private static ApplicationValue EncodeNode(JsonNode? value, int depth)
    {
        RequireDepth(depth);
        return value switch
        {
            null => new(ApplicationValueKind.Null),
            JsonObject source => EncodeObject(source, depth),
            JsonArray source => EncodeArray(source, depth),
            JsonValue scalar => EncodeScalar(scalar),
            _ => throw new InvalidOperationException("Unsupported application value."),
        };
    }

    private static ApplicationValue EncodeObject(JsonObject source, int depth)
    {
        var members = new List<ApplicationValueMember>(source.Count);
        foreach (var member in source)
            members.Add(new(EncodeText(member.Key), EncodeNode(member.Value, depth + 1)));
        return new(ApplicationValueKind.Map, Members: members);
    }

    private static ApplicationValue EncodeArray(JsonArray source, int depth)
    {
        var items = new List<ApplicationValue>(source.Count);
        foreach (var item in source)
            items.Add(EncodeNode(item, depth + 1));
        return new(ApplicationValueKind.Array, Items: items);
    }

    private static ApplicationValue EncodeScalar(JsonValue source)
    {
        if (source.TryGetValue<string>(out var text))
            return EncodeTextValue(text);
        if (source.TryGetValue<char>(out var character))
            return EncodeTextValue(character.ToString());
        return source.GetValueKind() switch
        {
            JsonValueKind.Number => new(ApplicationValueKind.Number, Number: source.ToJsonString()),
            JsonValueKind.True => new(ApplicationValueKind.Boolean, Boolean: true),
            JsonValueKind.False => new(ApplicationValueKind.Boolean, Boolean: false),
            JsonValueKind.Null => new(ApplicationValueKind.Null),
            JsonValueKind.String => EncodeTextValue(JsonSerializer.SerializeToElement(source).GetString()!),
            _ => throw new InvalidOperationException("Unsupported application scalar."),
        };
    }

    private static JsonNode? DecodeNode(ApplicationValue value, int depth)
    {
        RequireDepth(depth);
        RequireShape(value);
        return value.Kind switch
        {
            ApplicationValueKind.Null => null,
            ApplicationValueKind.Text => JsonValue.Create(value.Text ?? DecodeText(value.CodeUnits!.Value)),
            ApplicationValueKind.Boolean => JsonValue.Create(value.Boolean!.Value),
            ApplicationValueKind.Number => DecodeNumber(value.Number!),
            ApplicationValueKind.Map => DecodeObject(value.Members!, depth),
            ApplicationValueKind.Array => DecodeArray(value.Items!, depth),
            _ => throw new InvalidOperationException("Invalid application value discriminator."),
        };
    }

    private static JsonObject DecodeObject(IReadOnlyList<ApplicationValueMember> members, int depth)
    {
        var result = new JsonObject();
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            if (member is null || member.Value is null)
                throw new InvalidOperationException("Incomplete application object member.");
            result.Add(DecodeText(member.NameCodeUnits), DecodeNode(member.Value, depth + 1));
        }
        return result;
    }

    private static JsonArray DecodeArray(IReadOnlyList<ApplicationValue> items, int depth)
    {
        var result = new JsonArray();
        for (var index = 0; index < items.Count; index++)
            result.Add(DecodeNode(items[index] ?? throw new InvalidOperationException("Incomplete application array item."), depth + 1));
        return result;
    }

    private static JsonNode DecodeNumber(string number)
    {
        var value = JsonNode.Parse(number);
        if (value is not JsonValue || value.GetValueKind() != JsonValueKind.Number)
            throw new InvalidOperationException("Invalid application number.");
        return value;
    }

    private static void RequireShape(ApplicationValue value)
    {
        if (!Enum.IsDefined(value.Kind) || value.Kind == ApplicationValueKind.None
            || (value.Kind == ApplicationValueKind.Text) != (value.CodeUnits.HasValue || value.Text is not null)
            || value.CodeUnits.HasValue && value.Text is not null
            || value.Text is not null && !IsWellFormedText(value.Text)
            || (value.Kind == ApplicationValueKind.Number) != (value.Number is not null)
            || (value.Kind == ApplicationValueKind.Boolean) != value.Boolean.HasValue
            || (value.Kind == ApplicationValueKind.Map) != (value.Members is not null)
            || (value.Kind == ApplicationValueKind.Array) != (value.Items is not null))
            throw new InvalidOperationException("Inconsistent application value shape.");
    }

    private static ReadOnlyMemory<byte> EncodeText(string text)
    {
        var bytes = new byte[checked(text.Length * sizeof(char))];
        for (var index = 0; index < text.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * sizeof(char), sizeof(char)), text[index]);
        return bytes;
    }

    private static ApplicationValue EncodeTextValue(string text) =>
        IsWellFormedText(text) ? new(ApplicationValueKind.Text, Text: text)
            : new(ApplicationValueKind.Text, CodeUnits: EncodeText(text));

    private static bool IsWellFormedText(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (!char.IsSurrogate(character))
                continue;
            if (!char.IsHighSurrogate(character) || index + 1 == text.Length || !char.IsLowSurrogate(text[++index]))
                return false;
        }
        return true;
    }

    private static string DecodeText(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length % sizeof(char) != 0)
            throw new InvalidOperationException("Incomplete application text code unit.");
        return string.Create(bytes.Length / sizeof(char), bytes, static (characters, state) =>
        {
            for (var index = 0; index < characters.Length; index++)
                characters[index] = (char)BinaryPrimitives.ReadUInt16LittleEndian(state.Span.Slice(index * sizeof(char), sizeof(char)));
        });
    }

    private static void RequireDepth(int depth)
    {
        if (depth > 64)
            throw new InvalidOperationException("Application value nesting is too deep.");
    }
}
