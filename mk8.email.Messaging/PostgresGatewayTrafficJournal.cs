using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Storage;
using Npgsql;
using NpgsqlTypes;

namespace mk8.email.Messaging;

public sealed partial class PostgresGatewayTrafficJournal : IGatewayTrafficJournal
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IMessagingPayloadProtector _protector;
    private readonly ProtectedPayloadStorage _payloadStorage;
    private readonly PostgresMessagingOptions _options;
    private readonly ILogger<PostgresGatewayTrafficJournal> _logger;

    public PostgresGatewayTrafficJournal(
        NpgsqlDataSource dataSource,
        IMessagingPayloadProtector protector,
        PostgresMessagingOptions? options = null,
        ILargeObjectStore? largeObjectStore = null,
        ILogger<PostgresGatewayTrafficJournal>? logger = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _options = options ?? new PostgresMessagingOptions();
        _options.Validate();
        _payloadStorage = new ProtectedPayloadStorage(_options, largeObjectStore);
        _logger = logger ?? NullLogger<PostgresGatewayTrafficJournal>.Instance;
    }

    public async Task AppendAsync(
        GatewayTrafficRecord record,
        CancellationToken cancellationToken = default)
    {
        MessagingValues.ValidateTraffic(record, _options);
        var recordedAt = MessagingValues.NormalizeTimestamp(record.RecordedAt);
        var metadata = MessagingValues.ValidateAndSerializeMetadata(record.Metadata, _options);
        var associatedData = MessagingValues.TrafficAssociatedData(
            record.Id,
            record.SessionId,
            record.Sequence,
            record.Direction,
            record.Protocol,
            record.ContentType,
            metadata,
            recordedAt,
            record.ApplicationRequestId);
        var protectedPayload = _protector.Protect(record.Payload, associatedData);
        var storedPayload = await _payloadStorage.StoreAsync(
            protectedPayload,
            $"messaging/v1/gateway-traffic/{record.Id:D}/payload",
            cancellationToken).ConfigureAwait(false);

        var command = _dataSource.CreateCommand(
            """
            INSERT INTO gateway_traffic_records (
                id, session_id, sequence, direction, protocol, content_type,
                application_request_id, encryption_key_id, payload_inline,
                payload_blob_provider, payload_blob_name, payload_blob_etag,
                payload_length, payload_nonce, payload_tag, payload_sha256,
                metadata, recorded_at)
            VALUES (
                @id, @session_id, @sequence, @direction, @protocol, @content_type,
                @application_request_id, @encryption_key_id, @payload_inline,
                @payload_blob_provider, @payload_blob_name, @payload_blob_etag,
                @payload_length, @payload_nonce, @payload_tag, @payload_sha256,
                @metadata, @recorded_at)
            ON CONFLICT DO NOTHING
            """);
        await using var commandLifetime = command.ConfigureAwait(false);
        AddTrafficParameters(command, record, recordedAt, metadata, storedPayload);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted == 1)
            return;

        await TryDeleteUnreferencedAsync(storedPayload).ConfigureAwait(false);

        var existing = await ReadByIdentityAsync(
            record.Id,
            record.SessionId,
            record.Sequence,
            cancellationToken).ConfigureAwait(false);
        var normalized = record with { RecordedAt = recordedAt };
        if (existing is null || !Equivalent(existing, normalized))
            throw new InvalidOperationException("The gateway traffic identity is already in use.");

        LogIdempotentAppend(_logger, record.Id);
    }

    public async Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("The gateway session identifier is required.", nameof(sessionId));

        var command = _dataSource.CreateCommand(
            SelectColumns
            + " WHERE session_id = @session_id ORDER BY sequence, recorded_at, id");
        await using var commandLifetime = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("session_id", NpgsqlDbType.Uuid, sessionId);
        var reader = (await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false));
        await using var readerLifetime = reader.ConfigureAwait(false);
        var records = new List<GatewayTrafficRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            records.Add(await ReadRecordAsync(reader, cancellationToken).ConfigureAwait(false));
        return records;
    }

    private async Task<GatewayTrafficRecord?> ReadByIdentityAsync(
        Guid id,
        Guid sessionId,
        long sequence,
        CancellationToken cancellationToken)
    {
        var command = _dataSource.CreateCommand(
            SelectColumns
            + " WHERE id = @id OR (session_id = @session_id AND sequence = @sequence) LIMIT 1");
        await using var commandLifetime = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        command.Parameters.AddWithValue("session_id", NpgsqlDbType.Uuid, sessionId);
        command.Parameters.AddWithValue("sequence", NpgsqlDbType.Bigint, sequence);
        var reader = (await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false));
        await using var readerLifetime = reader.ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? await ReadRecordAsync(reader, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task<GatewayTrafficRecord> ReadRecordAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var id = reader.GetGuid(0);
        var sessionId = reader.GetGuid(1);
        var sequence = reader.GetInt64(2);
        var direction = reader.GetString(3);
        var protocol = reader.GetString(4);
        var contentType = reader.GetString(5);
        Guid? requestId = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
            ? null : reader.GetGuid(6);
        var metadata = MessagingValues.DeserializeMetadata(reader.GetString(16));
        var canonicalMetadata = MessagingValues.ValidateAndSerializeMetadata(metadata, _options);
        var recordedAt = ToDateTimeOffset(reader.GetDateTime(17));
        var storedPayload = new StoredProtectedPayload(
            reader.GetString(7),
            await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<byte[]>(8, cancellationToken).ConfigureAwait(false),
            ReadLargeObjectReference(reader, 9, 10, 11, 12, 15),
            await reader.GetFieldValueAsync<byte[]>(13, cancellationToken).ConfigureAwait(false),
            await reader.GetFieldValueAsync<byte[]>(14, cancellationToken).ConfigureAwait(false),
            reader.GetString(15),
            reader.GetInt64(12));
        var associatedData = MessagingValues.TrafficAssociatedData(
            id,
            sessionId,
            sequence,
            direction,
            protocol,
            contentType,
            canonicalMetadata,
            recordedAt,
            requestId);
        var protectedPayload = await _payloadStorage.LoadAsync(
            storedPayload,
            "gateway traffic",
            cancellationToken).ConfigureAwait(false);
        var payload = _protector.Unprotect(protectedPayload, associatedData);

        return new GatewayTrafficRecord(
            id,
            sessionId,
            sequence,
            direction,
            protocol,
            contentType,
            payload,
            metadata,
            recordedAt,
            requestId);
    }

    private static void AddTrafficParameters(
        NpgsqlCommand command,
        GatewayTrafficRecord record,
        DateTimeOffset recordedAt,
        string metadata,
        StoredProtectedPayload payload)
    {
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, record.Id);
        command.Parameters.AddWithValue("session_id", NpgsqlDbType.Uuid, record.SessionId);
        command.Parameters.AddWithValue("sequence", NpgsqlDbType.Bigint, record.Sequence);
        command.Parameters.AddWithValue("direction", NpgsqlDbType.Varchar, record.Direction);
        command.Parameters.AddWithValue("protocol", NpgsqlDbType.Varchar, record.Protocol);
        command.Parameters.AddWithValue("content_type", NpgsqlDbType.Varchar, record.ContentType);
        command.Parameters.AddWithValue(
            "application_request_id",
            NpgsqlDbType.Uuid,
            record.ApplicationRequestId is { } requestId ? requestId : DBNull.Value);
        command.Parameters.AddWithValue(
            "encryption_key_id",
            NpgsqlDbType.Varchar,
            payload.KeyId);
        AddPayloadParameters(command, payload);
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, metadata);
        command.Parameters.AddWithValue(
            "recorded_at",
            NpgsqlDbType.TimestampTz,
            recordedAt.UtcDateTime);
    }

    private static void AddPayloadParameters(NpgsqlCommand command, StoredProtectedPayload payload)
    {
        command.Parameters.AddWithValue(
            "payload_inline",
            NpgsqlDbType.Bytea,
            (object?)payload.InlineCiphertext ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "payload_blob_provider",
            NpgsqlDbType.Varchar,
            (object?)payload.LargeObject?.Provider ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "payload_blob_name",
            NpgsqlDbType.Varchar,
            (object?)payload.LargeObject?.ObjectName ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "payload_blob_etag",
            NpgsqlDbType.Varchar,
            (object?)payload.LargeObject?.EntityTag ?? DBNull.Value);
        command.Parameters.AddWithValue("payload_length", NpgsqlDbType.Bigint, payload.Length);
        command.Parameters.AddWithValue("payload_nonce", NpgsqlDbType.Bytea, payload.Nonce);
        command.Parameters.AddWithValue("payload_tag", NpgsqlDbType.Bytea, payload.Tag);
        command.Parameters.AddWithValue("payload_sha256", NpgsqlDbType.Char, payload.Sha256);
    }

    private static LargeObjectReference? ReadLargeObjectReference(
        NpgsqlDataReader reader,
        int providerIndex,
        int objectNameIndex,
        int entityTagIndex,
        int lengthIndex,
        int hashIndex) =>
        reader.IsDBNull(providerIndex)
            ? null
            : new LargeObjectReference(
                reader.GetString(providerIndex),
                reader.GetString(objectNameIndex),
                reader.GetInt64(lengthIndex),
                reader.GetString(hashIndex),
                reader.GetString(entityTagIndex));

    private async Task TryDeleteUnreferencedAsync(StoredProtectedPayload payload)
    {
        try
        {
            await _payloadStorage.DeleteIfCreatedAsync(payload, CancellationToken.None).ConfigureAwait(false);
        }
        // The journal row is authoritative; failed orphan cleanup is logged for retention repair.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogOrphanCleanupFailure(_logger, exception);
        }
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    private static bool Equivalent(GatewayTrafficRecord left, GatewayTrafficRecord right) =>
        left.Id == right.Id
        && left.SessionId == right.SessionId
        && left.Sequence == right.Sequence
        && string.Equals(left.Direction, right.Direction, StringComparison.Ordinal)
        && string.Equals(left.Protocol, right.Protocol, StringComparison.Ordinal)
        && string.Equals(left.ContentType, right.ContentType, StringComparison.Ordinal)
        && left.Payload.AsSpan().SequenceEqual(right.Payload)
        && left.Metadata.Count == right.Metadata.Count
        && left.Metadata.All(item => right.Metadata.TryGetValue(item.Key, out var value)
            && string.Equals(value, item.Value, StringComparison.Ordinal))
        && left.RecordedAt == right.RecordedAt
        && left.ApplicationRequestId == right.ApplicationRequestId;

    private const string SelectColumns = """
        SELECT id, session_id, sequence, direction, protocol, content_type,
            application_request_id, encryption_key_id, payload_inline,
            payload_blob_provider, payload_blob_name, payload_blob_etag,
            payload_length, payload_nonce, payload_tag, payload_sha256,
            metadata::text, recorded_at
        FROM gateway_traffic_records
        """;

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Debug,
        Message = "Accepted an idempotent gateway traffic append for {TrafficId}")]
    private static partial void LogIdempotentAppend(ILogger logger, Guid trafficId);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Warning,
        Message = "Could not remove an unreferenced gateway traffic large object")]
    private static partial void LogOrphanCleanupFailure(ILogger logger, Exception exception);
}
