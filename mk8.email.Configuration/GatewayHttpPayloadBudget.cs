namespace mk8.email.Configuration;

public static class GatewayHttpPayloadBudget
{
    // Worst-case JSON escaping of the bounded Kestrel headers/request line,
    // plus envelope keys, delimiters, content type and transport authentication.
    public const int MetadataBytes = 256 * 1024;
    public const int RequestHeadersBytes = 32 * 1024;
    public const int RequestLineBytes = 8 * 1024;
    public const int SmallRequestBytes = 64 * 1024;

    public static long BinaryEnvelopeBytes(long bodyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bodyBytes);
        return checked(4 * ((bodyBytes + 2) / 3) + MetadataBytes);
    }

    public static long TextEnvelopeBytes(long bodyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bodyBytes);
        return checked(6 * bodyBytes + MetadataBytes);
    }

    public static long MaximumBinaryBodyBytes(int payloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBytes);
        return Math.Max(0, 3L * ((payloadBytes - MetadataBytes) / 4));
    }
}
