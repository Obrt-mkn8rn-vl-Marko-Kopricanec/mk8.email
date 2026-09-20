using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Jmap;

internal static class JmapJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static async Task<JsonNode?> ParseRequestAsync(
        HttpRequest request,
        JmapConfig configuration,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > configuration.MaxRequestSizeBytes)
            throw Limit();

        await using var buffer = new MemoryStream();
        var rented = new byte[16 * 1024];
        long total = 0;
        while (true)
        {
            var read = await request.Body.ReadAsync(rented, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > configuration.MaxRequestSizeBytes)
                throw Limit();
            await buffer.WriteAsync(rented.AsMemory(0, read), cancellationToken);
        }

        if (buffer.Length == 0)
            throw NotJson("The request body is empty.");

        try
        {
            var bytes = buffer.ToArray();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            if (!HasUniqueObjectProperties(document.RootElement))
                throw NotJson("JSON objects must not contain duplicate property names.");
            if (!HasValidNumbers(document.RootElement))
                throw NotJson("JSON numbers must be finite IEEE 754 values.");
            if (!HasValidUnicode(document.RootElement))
                throw NotJson("JSON strings must contain only valid Unicode scalar values.");

            return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        }
        catch (JmapRequestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw NotJson("The request body is not valid I-JSON.", exception);
        }
    }

    private static bool HasUniqueObjectProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) || !HasUniqueObjectProperties(property.Value))
                        return false;
                }
                return true;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!HasUniqueObjectProperties(item))
                        return false;
                }
                return true;
            default:
                return true;
        }
    }

    private static bool HasValidNumbers(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return element.EnumerateObject().All(property => HasValidNumbers(property.Value));
            case JsonValueKind.Array:
                return element.EnumerateArray().All(HasValidNumbers);
            case JsonValueKind.Number:
                return element.TryGetDouble(out var value) && double.IsFinite(value);
            default:
                return true;
        }
    }

    private static bool HasValidUnicode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (!ContainsOnlyUnicodeScalars(property.Name)
                        || !HasValidUnicode(property.Value))
                    {
                        return false;
                    }
                }
                return true;
            case JsonValueKind.Array:
                return element.EnumerateArray().All(HasValidUnicode);
            case JsonValueKind.String:
                return ContainsOnlyUnicodeScalars(element.GetString()!);
            default:
                return true;
        }
    }

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

    private static JmapRequestException Limit() =>
        new(
            "urn:ietf:params:jmap:error:limit",
            StatusCodes.Status400BadRequest,
            "Request limit exceeded",
            "The JMAP request is larger than the server limit.",
            "maxSizeRequest");

    private static JmapRequestException NotJson(string detail, Exception? innerException = null)
    {
        var exception = new JmapRequestException(
            "urn:ietf:params:jmap:error:notJSON",
            StatusCodes.Status400BadRequest,
            "Invalid JSON",
            detail);
        if (innerException is not null)
            exception.Data[nameof(innerException)] = innerException.GetType().Name;
        return exception;
    }
}
