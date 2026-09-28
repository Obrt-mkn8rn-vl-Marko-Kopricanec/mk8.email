using System.Buffers;
using System.Text;
using System.Text.Json;

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
}
