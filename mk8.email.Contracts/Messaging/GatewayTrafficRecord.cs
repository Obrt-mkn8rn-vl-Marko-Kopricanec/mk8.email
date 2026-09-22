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
