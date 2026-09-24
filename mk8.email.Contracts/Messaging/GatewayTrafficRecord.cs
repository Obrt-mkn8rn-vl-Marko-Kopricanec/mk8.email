// Protocol request/result types are deliberately grouped in this transport-contract file; array fields are part of the established JSON/public API.
#pragma warning disable MA0048, CA1819
namespace mk8.email.Contracts.Messaging;

public static class GatewayTrafficDirections
{
    public const string Inbound = "inbound";
    public const string Outbound = "outbound";
}

public sealed record GatewayTrafficRecord(
    Guid Id,
    Guid SessionId,
    long Sequence,
    string Direction,
    string Protocol,
    string ContentType,
    byte[] Payload,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset RecordedAt,
    Guid? ApplicationRequestId = null);
