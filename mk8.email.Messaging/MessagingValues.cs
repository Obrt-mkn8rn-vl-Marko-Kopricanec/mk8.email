using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging;

internal static partial class MessagingValues
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static string ValidateAndSerializeMetadata(
        IReadOnlyDictionary<string, string> metadata,
        PostgresMessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Count > 64)
            throw new ArgumentException("Messaging metadata cannot contain more than 64 entries.", nameof(metadata));
        foreach (var item in metadata)
        {
            if (string.IsNullOrEmpty(item.Key)
                || item.Key.Length > 64
                || !MetadataKeyPattern().IsMatch(item.Key))
            {
                throw new ArgumentException("A messaging metadata key is invalid.", nameof(metadata));
            }
            if (item.Value is null || item.Value.Length > 4096 || item.Value.Contains('\0'))
                throw new ArgumentException("A messaging metadata value is invalid.", nameof(metadata));
        }

        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in metadata)
            sorted.Add(item.Key, item.Value);
        var json = JsonSerializer.Serialize(sorted, MetadataJsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > options.MaxMetadataBytes)
            throw new ArgumentException("Messaging metadata is too large.", nameof(metadata));
        return json;
    }

    public static IReadOnlyDictionary<string, string> DeserializeMetadata(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, MetadataJsonOptions)
            ?? throw new InvalidOperationException("Stored messaging metadata is invalid.");
    }

    public static void ValidateRequest(ApplicationRequest request, PostgresMessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.Id, request.SessionId, request.Sequence);
        ValidateProtocol(request.Protocol);
        ValidateOperation(request.Operation);
        ValidateContentType(request.ContentType);
        ValidatePayload(request.Payload, options);
        _ = ValidateAndSerializeMetadata(request.Metadata, options);
        if (request.CreatedAt == default || request.Deadline <= request.CreatedAt)
            throw new ArgumentException("The application request deadline is invalid.", nameof(request));
        if (request.IdempotencyKey is { } key
            && (key.Length is < 1 or > 128 || key.Any(char.IsControl)))
        {
            throw new ArgumentException("The application request idempotency key is invalid.", nameof(request));
        }
    }

    public static void ValidateResponse(
        ApplicationResponse response,
        PostgresMessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.RequestId == Guid.Empty)
            throw new ArgumentException("The application response request identifier is required.", nameof(response));
        ValidateContentType(response.ContentType);
        ValidatePayload(response.Payload, options);
        _ = ValidateAndSerializeMetadata(response.Metadata, options);
        if (response.IsError
            && (string.IsNullOrWhiteSpace(response.ErrorCode)
                || response.ErrorCode.Length > 128
                || !OperationPattern().IsMatch(response.ErrorCode)))
        {
            throw new ArgumentException("An error response requires a valid error code.", nameof(response));
        }
        if (!response.IsError && response.ErrorCode is not null)
            throw new ArgumentException("A successful response cannot contain an error code.", nameof(response));
        if (response.ErrorDetail is { Length: > 2048 } || response.ErrorDetail?.Contains('\0') == true)
            throw new ArgumentException("The application response error detail is invalid.", nameof(response));
    }

    public static void ValidateTraffic(
        GatewayTrafficRecord record,
        PostgresMessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateIdentity(record.Id, record.SessionId, record.Sequence);
        if (record.Direction is not GatewayTrafficDirections.Inbound
            and not GatewayTrafficDirections.Outbound)
        {
            throw new ArgumentException("The gateway traffic direction is invalid.", nameof(record));
        }
        ValidateProtocol(record.Protocol);
        ValidateContentType(record.ContentType);
        ValidatePayload(record.Payload, options);
        _ = ValidateAndSerializeMetadata(record.Metadata, options);
        if (record.RecordedAt == default)
            throw new ArgumentException("The gateway traffic timestamp is required.", nameof(record));
    }

    public static void ValidateWorkerId(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId)
            || workerId.Length > 128
            || !WorkerIdPattern().IsMatch(workerId))
        {
            throw new ArgumentException("The application worker identifier is invalid.", nameof(workerId));
        }
    }

    public static void ValidateFailure(string errorCode, string errorDetail)
    {
        if (string.IsNullOrWhiteSpace(errorCode)
            || errorCode.Length > 128
            || !OperationPattern().IsMatch(errorCode))
        {
            throw new ArgumentException("The application failure code is invalid.", nameof(errorCode));
        }
        if (string.IsNullOrWhiteSpace(errorDetail)
            || errorDetail.Length > 2048
            || errorDetail.Contains('\0'))
        {
            throw new ArgumentException("The application failure detail is invalid.", nameof(errorDetail));
        }
    }

    public static string Sha256(byte[] payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));

    public static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks - value.UtcTicks % 10;
        return new DateTimeOffset(utcTicks, TimeSpan.Zero);
    }

    public static byte[] TrafficAssociatedData(
        Guid id,
        Guid sessionId,
        long sequence,
        string direction,
        string protocol,
        string contentType,
        string metadata,
        DateTimeOffset recordedAt,
        Guid? applicationRequestId) =>
        BuildAssociatedData(
            "gateway-traffic-v1",
            id.ToString("D"),
            sessionId.ToString("D"),
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            direction,
            protocol,
            contentType,
            metadata,
            recordedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            applicationRequestId?.ToString("D") ?? string.Empty);

    public static byte[] RequestAssociatedData(
        Guid id,
        Guid sessionId,
        long sequence,
        string protocol,
        string operation,
        string contentType,
        string metadata,
        string? idempotencyKey,
        DateTimeOffset createdAt,
        DateTimeOffset deadline,
        string domain) =>
        BuildAssociatedData(
            domain,
            id.ToString("D"),
            sessionId.ToString("D"),
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            protocol,
            operation,
            contentType,
            metadata,
            idempotencyKey ?? string.Empty,
            createdAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            deadline.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static byte[] ResponseAssociatedData(
        Guid requestId,
        string contentType,
        string metadata,
        bool isError,
        string? errorCode,
        string? errorDetail,
        string domain) =>
        BuildAssociatedData(
            domain,
            requestId.ToString("D"),
            contentType,
            metadata,
            isError ? "true" : "false",
            errorCode ?? string.Empty,
            errorDetail ?? string.Empty);

    private static void ValidateIdentity(Guid id, Guid sessionId, long sequence)
    {
        if (id == Guid.Empty || sessionId == Guid.Empty || sequence < 0)
            throw new ArgumentException("The messaging identity is invalid.");
    }

    private static void ValidateProtocol(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol)
            || protocol.Length > 32
            || !ProtocolPattern().IsMatch(protocol))
        {
            throw new ArgumentException("The messaging protocol is invalid.", nameof(protocol));
        }
    }

    private static void ValidateOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation)
            || operation.Length > 128
            || !OperationPattern().IsMatch(operation))
        {
            throw new ArgumentException("The application operation is invalid.", nameof(operation));
        }
    }

    private static void ValidateContentType(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)
            || contentType.Length > 255
            || contentType.Any(char.IsControl))
        {
            throw new ArgumentException("The messaging content type is invalid.", nameof(contentType));
        }
    }

    private static void ValidatePayload(byte[] payload, PostgresMessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > options.MaxPayloadBytes)
            throw new ArgumentException("The messaging payload is too large.", nameof(payload));
    }

    private static byte[] BuildAssociatedData(params string[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in values)
                writer.Write(value);
        }
        return stream.ToArray();
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProtocolPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex MetadataKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:@/-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkerIdPattern();
}
