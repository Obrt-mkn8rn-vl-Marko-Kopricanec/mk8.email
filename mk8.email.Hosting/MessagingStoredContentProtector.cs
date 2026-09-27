using System.Text.Json;
using mk8.email.Contracts.Storage;
using mk8.email.Messaging;

namespace mk8.email.Hosting;

public sealed class MessagingStoredContentProtector(IMessagingPayloadProtector protector) : IStoredContentProtector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    public ReadOnlyMemory<byte> Protect(ReadOnlySpan<byte> content, ReadOnlySpan<byte> associatedData) =>
        JsonSerializer.SerializeToUtf8Bytes(protector.Protect(content, associatedData), JsonOptions);

    public ReadOnlyMemory<byte> Unprotect(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> associatedData)
    {
        var payload = JsonSerializer.Deserialize<ProtectedPayload>(envelope, JsonOptions)
            ?? throw new InvalidOperationException("The protected stored content is incomplete.");
        return protector.Unprotect(payload, associatedData);
    }
}
