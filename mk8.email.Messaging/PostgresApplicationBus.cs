using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Storage;
using Npgsql;
using NpgsqlTypes;

namespace mk8.email.Messaging;

public sealed class PostgresApplicationBus : IApplicationRequestClient, IApplicationRequestConsumer
{
    private const string RequestChannel = "mk8_application_request";
    private const string ResponseChannel = "mk8_application_response";
    private readonly NpgsqlDataSource _dataSource;
    private readonly IMessagingPayloadProtector _protector;
    private readonly ProtectedPayloadStorage _payloadStorage;
    private readonly PostgresMessagingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PostgresApplicationBus> _logger;

    public PostgresApplicationBus(
        NpgsqlDataSource dataSource,
        IMessagingPayloadProtector protector,
        PostgresMessagingOptions? options = null,
        TimeProvider? timeProvider = null,
        ILargeObjectStore? largeObjectStore = null,
        ILogger<PostgresApplicationBus>? logger = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _options = options ?? new PostgresMessagingOptions();
        _options.Validate();
        _payloadStorage = new ProtectedPayloadStorage(_options, largeObjectStore);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<PostgresApplicationBus>.Instance;
    }

    public async Task EnqueueAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        MessagingValues.ValidateRequest(request, _options);
        var createdAt = MessagingValues.NormalizeTimestamp(request.CreatedAt);
        var deadline = MessagingValues.NormalizeTimestamp(request.Deadline);
        var metadata = MessagingValues.ValidateAndSerializeMetadata(request.Metadata, _options);
        var associatedData = MessagingValues.RequestAssociatedData(
            request.Id,
            request.SessionId,
            request.Sequence,
            request.Protocol,
            request.Operation,
            request.ContentType,
            metadata,
            request.IdempotencyKey,
            createdAt,
            deadline);
        var protectedPayload = _protector.Protect(request.Payload, associatedData);
        var storedPayload = await _payloadStorage.StoreAsync(
            protectedPayload,
            $"messaging/v1/application-requests/{request.Id:D}/request",
            cancellationToken);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO application_requests (
                id, session_id, sequence, protocol, operation, request_content_type,
                request_encryption_key_id, request_payload_inline,
                request_payload_blob_provider, request_payload_blob_name,
                request_payload_blob_etag, request_payload_length,
                request_payload_nonce, request_payload_tag, request_payload_sha256,
                request_metadata,
                idempotency_key, created_at, deadline_at)
            VALUES (
                @id, @session_id, @sequence, @protocol, @operation, @content_type,
                @encryption_key_id, @payload_inline, @payload_blob_provider,
                @payload_blob_name, @payload_blob_etag, @payload_length,
                @payload_nonce, @payload_tag, @payload_sha256, @metadata,
                @idempotency_key, @created_at, @deadline_at)
            ON CONFLICT DO NOTHING
            """;
        AddRequestParameters(
            command,
            request,
            createdAt,
            deadline,
            metadata,
            storedPayload);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (inserted == 1)
            await NotifyAsync(connection, transaction, RequestChannel, request.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (inserted == 1)
            return;

        await TryDeleteUnreferencedAsync(storedPayload, "application request");

        var existing = await ReadByIdentityAsync(request, cancellationToken);
        var normalized = request with { CreatedAt = createdAt, Deadline = deadline };
        if (existing is null || !Equivalent(existing.Request, normalized))
            throw new InvalidOperationException("The application request identity is already in use.");

        _logger.LogDebug(
            "Accepted an idempotent application request enqueue for {RequestId}",
            request.Id);
    }

    public async Task<ApplicationResponse> SendAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnqueueAsync(request, cancellationToken);
        return await WaitForResponseAsync(request.Id, request.Deadline, cancellationToken);
    }

    public async Task<ApplicationResponse> WaitForResponseAsync(
        Guid requestId,
        DateTimeOffset deadline,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("The application request identifier is required.", nameof(requestId));
        if (deadline == default)
            throw new ArgumentException("The application request deadline is required.", nameof(deadline));

        await using var listener = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var listen = listener.CreateCommand())
        {
            listen.CommandText = $"LISTEN {ResponseChannel}";
            await listen.ExecuteNonQueryAsync(cancellationToken);
        }

        while (true)
        {
            var snapshot = await GetAsync(requestId, cancellationToken)
                ?? throw new InvalidOperationException("The application request does not exist.");
            if (snapshot.State == ApplicationExchangeStates.Completed)
                return snapshot.Response
                    ?? throw new InvalidOperationException("The completed application response is missing.");
            if (snapshot.State == ApplicationExchangeStates.Failed)
            {
                throw new ApplicationRequestFailedException(
                    requestId,
                    snapshot.FailureCode ?? "application-failed",
                    snapshot.FailureDetail);
            }
            if (snapshot.State == ApplicationExchangeStates.Expired)
                throw new ApplicationRequestExpiredException(requestId);

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                await ExpireDueRequestsAsync(cancellationToken);
                throw new ApplicationRequestExpiredException(requestId);
            }

            var wait = remaining < _options.NotificationFallbackInterval
                ? remaining
                : _options.NotificationFallbackInterval;
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCancellation.CancelAfter(wait);
            try
            {
                await listener.WaitAsync(waitCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A bounded fallback recheck recovers safely from a missed PostgreSQL notification.
            }
        }
    }

    public async Task<ApplicationRequestLease> WaitForRequestAsync(
        string workerId,
        CancellationToken cancellationToken = default)
    {
        MessagingValues.ValidateWorkerId(workerId);
        await using var listener = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var listen = listener.CreateCommand())
        {
            listen.CommandText = $"LISTEN {RequestChannel}";
            await listen.ExecuteNonQueryAsync(cancellationToken);
        }

        while (true)
        {
            var claimed = await TryClaimAsync(workerId, cancellationToken);
            if (claimed is not null)
                return claimed;

            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCancellation.CancelAfter(_options.NotificationFallbackInterval);
            try
            {
                await listener.WaitAsync(waitCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The worker remains dormant between notifications, with a bounded lease-recovery scan.
            }
        }
    }

    public async Task<ApplicationRequestLease?> TryClaimAsync(
        string workerId,
        CancellationToken cancellationToken = default)
    {
        MessagingValues.ValidateWorkerId(workerId);
        await ExpireDueRequestsAsync(cancellationToken);

        await using var command = _dataSource.CreateCommand(
            """
            WITH candidate AS (
                SELECT id
                FROM application_requests
                WHERE deadline_at > clock_timestamp()
                    AND (
                        state = 'pending'
                        OR (state = 'processing' AND lease_expires_at <= clock_timestamp()))
                ORDER BY created_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE application_requests AS request
            SET state = 'processing',
                attempt_count = request.attempt_count + 1,
                lease_owner = @worker_id,
                lease_expires_at = clock_timestamp() + @lease_milliseconds * interval '1 millisecond'
            FROM candidate
            WHERE request.id = candidate.id
            RETURNING
            """
            + "\n"
            + ReturningColumns);
        command.Parameters.AddWithValue("worker_id", NpgsqlDbType.Varchar, workerId);
        command.Parameters.AddWithValue(
            "lease_milliseconds",
            NpgsqlDbType.Integer,
            checked((int)_options.LeaseDuration.TotalMilliseconds));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var snapshot = await ReadSnapshotAsync(reader, cancellationToken);
        return new ApplicationRequestLease(
            snapshot.Request,
            snapshot.LeaseOwner
                ?? throw new InvalidOperationException("The claimed application request has no worker."),
            snapshot.LeaseExpiresAt
                ?? throw new InvalidOperationException("The claimed application request has no lease."),
            snapshot.AttemptCount);
    }

    public async Task<bool> RenewLeaseAsync(
        ApplicationRequestLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        MessagingValues.ValidateWorkerId(lease.WorkerId);
        await using var command = _dataSource.CreateCommand(
            """
            UPDATE application_requests
            SET lease_expires_at = clock_timestamp() + @lease_milliseconds * interval '1 millisecond'
            WHERE id = @id
                AND state = 'processing'
                AND lease_owner = @worker_id
                AND lease_expires_at > clock_timestamp()
                AND deadline_at > clock_timestamp()
            """);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, lease.Request.Id);
        command.Parameters.AddWithValue("worker_id", NpgsqlDbType.Varchar, lease.WorkerId);
        command.Parameters.AddWithValue(
            "lease_milliseconds",
            NpgsqlDbType.Integer,
            checked((int)_options.LeaseDuration.TotalMilliseconds));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task CompleteAsync(
        ApplicationRequestLease lease,
        ApplicationResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        MessagingValues.ValidateWorkerId(lease.WorkerId);
        MessagingValues.ValidateResponse(response, _options);
        if (response.RequestId != lease.Request.Id)
            throw new ArgumentException("The response does not match the leased request.", nameof(response));

        var metadata = MessagingValues.ValidateAndSerializeMetadata(response.Metadata, _options);
        var associatedData = MessagingValues.ResponseAssociatedData(
            response.RequestId,
            response.ContentType,
            metadata,
            response.IsError,
            response.ErrorCode,
            response.ErrorDetail);
        var protectedPayload = _protector.Protect(response.Payload, associatedData);
        var storedPayload = await _payloadStorage.StoreAsync(
            protectedPayload,
            $"messaging/v1/application-requests/{response.RequestId:D}/response",
            cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE application_requests
            SET state = 'completed',
                lease_owner = NULL,
                lease_expires_at = NULL,
                response_content_type = @content_type,
                response_encryption_key_id = @encryption_key_id,
                response_payload_inline = @payload_inline,
                response_payload_blob_provider = @payload_blob_provider,
                response_payload_blob_name = @payload_blob_name,
                response_payload_blob_etag = @payload_blob_etag,
                response_payload_length = @payload_length,
                response_payload_nonce = @payload_nonce,
                response_payload_tag = @payload_tag,
                response_payload_sha256 = @payload_sha256,
                response_metadata = @metadata,
                response_is_error = @is_error,
                error_code = @error_code,
                error_detail = @error_detail,
                completed_at = clock_timestamp()
            WHERE id = @id
                AND state = 'processing'
                AND lease_owner = @worker_id
                AND lease_expires_at > clock_timestamp()
                AND deadline_at > clock_timestamp()
            """;
        AddResponseParameters(command, lease, response, metadata, storedPayload);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await TryDeleteUnreferencedAsync(storedPayload, "application response");
            throw new ApplicationRequestLeaseLostException(lease.Request.Id);
        }
        await NotifyAsync(connection, transaction, ResponseChannel, lease.Request.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FailAsync(
        ApplicationRequestLease lease,
        string errorCode,
        string errorDetail,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        MessagingValues.ValidateWorkerId(lease.WorkerId);
        MessagingValues.ValidateFailure(errorCode, errorDetail);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE application_requests
            SET state = 'failed',
                lease_owner = NULL,
                lease_expires_at = NULL,
                error_code = @error_code,
                error_detail = @error_detail,
                completed_at = clock_timestamp()
            WHERE id = @id
                AND state = 'processing'
                AND lease_owner = @worker_id
                AND lease_expires_at > clock_timestamp()
            """;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, lease.Request.Id);
        command.Parameters.AddWithValue("worker_id", NpgsqlDbType.Varchar, lease.WorkerId);
        command.Parameters.AddWithValue("error_code", NpgsqlDbType.Varchar, errorCode);
        command.Parameters.AddWithValue("error_detail", NpgsqlDbType.Varchar, errorDetail);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new ApplicationRequestLeaseLostException(lease.Request.Id);
        await NotifyAsync(connection, transaction, ResponseChannel, lease.Request.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ApplicationExchangeSnapshot?> GetAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("The application request identifier is required.", nameof(requestId));
        await using var command = _dataSource.CreateCommand(
            SelectColumns + " WHERE id = @id LIMIT 1");
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? await ReadSnapshotAsync(reader, cancellationToken)
            : null;
    }

    private async Task<ApplicationExchangeSnapshot?> ReadByIdentityAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            SelectColumns
            + """
             WHERE id = @id
                OR (session_id = @session_id AND sequence = @sequence)
                OR (@idempotency_key IS NOT NULL
                    AND protocol = @protocol
                    AND idempotency_key = @idempotency_key)
             LIMIT 1
            """);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, request.Id);
        command.Parameters.AddWithValue("session_id", NpgsqlDbType.Uuid, request.SessionId);
        command.Parameters.AddWithValue("sequence", NpgsqlDbType.Bigint, request.Sequence);
        command.Parameters.AddWithValue("protocol", NpgsqlDbType.Varchar, request.Protocol);
        command.Parameters.AddWithValue(
            "idempotency_key",
            NpgsqlDbType.Varchar,
            (object?)request.IdempotencyKey ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? await ReadSnapshotAsync(reader, cancellationToken)
            : null;
    }

    private async Task ExpireDueRequestsAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            $"""
            WITH expired AS (
                UPDATE application_requests
                SET state = 'expired',
                    lease_owner = NULL,
                    lease_expires_at = NULL,
                    error_code = 'request-expired',
                    error_detail = 'The application request deadline elapsed.',
                    completed_at = clock_timestamp()
                WHERE state IN ('pending', 'processing')
                    AND deadline_at <= clock_timestamp()
                RETURNING id
            )
            SELECT pg_notify('{ResponseChannel}', id::text) FROM expired
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ApplicationExchangeSnapshot> ReadSnapshotAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var id = reader.GetGuid(0);
        var sessionId = reader.GetGuid(1);
        var sequence = reader.GetInt64(2);
        var protocol = reader.GetString(3);
        var operation = reader.GetString(4);
        var contentType = reader.GetString(5);
        var requestMetadata = MessagingValues.DeserializeMetadata(reader.GetString(15));
        var canonicalRequestMetadata = MessagingValues.ValidateAndSerializeMetadata(
            requestMetadata,
            _options);
        var idempotencyKey = reader.IsDBNull(16) ? null : reader.GetString(16);
        var createdAt = ToDateTimeOffset(reader.GetDateTime(17));
        var deadline = ToDateTimeOffset(reader.GetDateTime(18));
        var storedRequestPayload = new StoredProtectedPayload(
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7),
            ReadLargeObjectReference(reader, 8, 9, 10, 11, 14),
            reader.GetFieldValue<byte[]>(12),
            reader.GetFieldValue<byte[]>(13),
            reader.GetString(14),
            reader.GetInt64(11));
        var requestAssociatedData = MessagingValues.RequestAssociatedData(
            id,
            sessionId,
            sequence,
            protocol,
            operation,
            contentType,
            canonicalRequestMetadata,
            idempotencyKey,
            createdAt,
            deadline);
        var protectedRequestPayload = await _payloadStorage.LoadAsync(
            storedRequestPayload,
            "application request",
            cancellationToken);
        var requestPayload = _protector.Unprotect(
            protectedRequestPayload,
            requestAssociatedData);
        var request = new ApplicationRequest(
            id,
            sessionId,
            sequence,
            protocol,
            operation,
            contentType,
            requestPayload,
            requestMetadata,
            createdAt,
            deadline,
            idempotencyKey);

        var state = reader.GetString(19);
        ApplicationResponse? response = null;
        if (state == ApplicationExchangeStates.Completed)
        {
            var responseContentType = reader.GetString(23);
            var responseMetadata = MessagingValues.DeserializeMetadata(reader.GetString(33));
            var canonicalResponseMetadata = MessagingValues.ValidateAndSerializeMetadata(
                responseMetadata,
                _options);
            var responseIsError = reader.GetBoolean(34);
            var responseErrorCode = reader.IsDBNull(35) ? null : reader.GetString(35);
            var responseErrorDetail = reader.IsDBNull(36) ? null : reader.GetString(36);
            var storedResponsePayload = new StoredProtectedPayload(
                reader.GetString(24),
                reader.IsDBNull(25) ? null : reader.GetFieldValue<byte[]>(25),
                ReadLargeObjectReference(reader, 26, 27, 28, 29, 32),
                reader.GetFieldValue<byte[]>(30),
                reader.GetFieldValue<byte[]>(31),
                reader.GetString(32),
                reader.GetInt64(29));
            var protectedResponsePayload = await _payloadStorage.LoadAsync(
                storedResponsePayload,
                "application response",
                cancellationToken);
            var responsePayload = _protector.Unprotect(
                protectedResponsePayload,
                MessagingValues.ResponseAssociatedData(
                    id,
                    responseContentType,
                    canonicalResponseMetadata,
                    responseIsError,
                    responseErrorCode,
                    responseErrorDetail));
            response = new ApplicationResponse(
                id,
                responseContentType,
                responsePayload,
                responseMetadata,
                responseIsError,
                responseErrorCode,
                responseErrorDetail);
        }

        return new ApplicationExchangeSnapshot(
            request,
            state,
            reader.GetInt32(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            reader.IsDBNull(22) ? null : ToDateTimeOffset(reader.GetDateTime(22)),
            response,
            reader.IsDBNull(37) ? null : ToDateTimeOffset(reader.GetDateTime(37)),
            state is ApplicationExchangeStates.Failed or ApplicationExchangeStates.Expired
                ? reader.GetString(35)
                : null,
            state is ApplicationExchangeStates.Failed or ApplicationExchangeStates.Expired
                ? reader.GetString(36)
                : null);
    }

    private static void AddRequestParameters(
        NpgsqlCommand command,
        ApplicationRequest request,
        DateTimeOffset createdAt,
        DateTimeOffset deadline,
        string metadata,
        StoredProtectedPayload payload)
    {
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, request.Id);
        command.Parameters.AddWithValue("session_id", NpgsqlDbType.Uuid, request.SessionId);
        command.Parameters.AddWithValue("sequence", NpgsqlDbType.Bigint, request.Sequence);
        command.Parameters.AddWithValue("protocol", NpgsqlDbType.Varchar, request.Protocol);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Varchar, request.Operation);
        command.Parameters.AddWithValue("content_type", NpgsqlDbType.Varchar, request.ContentType);
        command.Parameters.AddWithValue(
            "encryption_key_id",
            NpgsqlDbType.Varchar,
            payload.KeyId);
        AddPayloadParameters(command, payload);
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, metadata);
        command.Parameters.AddWithValue(
            "idempotency_key",
            NpgsqlDbType.Varchar,
            (object?)request.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, createdAt.UtcDateTime);
        command.Parameters.AddWithValue("deadline_at", NpgsqlDbType.TimestampTz, deadline.UtcDateTime);
    }

    private static void AddResponseParameters(
        NpgsqlCommand command,
        ApplicationRequestLease lease,
        ApplicationResponse response,
        string metadata,
        StoredProtectedPayload payload)
    {
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, lease.Request.Id);
        command.Parameters.AddWithValue("worker_id", NpgsqlDbType.Varchar, lease.WorkerId);
        command.Parameters.AddWithValue("content_type", NpgsqlDbType.Varchar, response.ContentType);
        command.Parameters.AddWithValue(
            "encryption_key_id",
            NpgsqlDbType.Varchar,
            payload.KeyId);
        AddPayloadParameters(command, payload);
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, metadata);
        command.Parameters.AddWithValue("is_error", NpgsqlDbType.Boolean, response.IsError);
        command.Parameters.AddWithValue(
            "error_code",
            NpgsqlDbType.Varchar,
            (object?)response.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "error_detail",
            NpgsqlDbType.Varchar,
            (object?)response.ErrorDetail ?? DBNull.Value);
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

    private static async Task NotifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string channel,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var notify = connection.CreateCommand();
        notify.Transaction = transaction;
        notify.CommandText = "SELECT pg_notify(@channel, @payload)";
        notify.Parameters.AddWithValue("channel", NpgsqlDbType.Text, channel);
        notify.Parameters.AddWithValue("payload", NpgsqlDbType.Text, requestId.ToString("D"));
        await notify.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task TryDeleteUnreferencedAsync(
        StoredProtectedPayload payload,
        string description)
    {
        try
        {
            await _payloadStorage.DeleteIfCreatedAsync(payload, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not remove an unreferenced {PayloadDescription} large object",
                description);
        }
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    private static bool Equivalent(ApplicationRequest left, ApplicationRequest right) =>
        left.Id == right.Id
        && left.SessionId == right.SessionId
        && left.Sequence == right.Sequence
        && left.Protocol == right.Protocol
        && left.Operation == right.Operation
        && left.ContentType == right.ContentType
        && left.Payload.AsSpan().SequenceEqual(right.Payload)
        && left.Metadata.Count == right.Metadata.Count
        && left.Metadata.All(item => right.Metadata.TryGetValue(item.Key, out var value) && value == item.Value)
        && left.CreatedAt == right.CreatedAt
        && left.Deadline == right.Deadline
        && left.IdempotencyKey == right.IdempotencyKey;

    private const string SelectColumns = """
        SELECT id, session_id, sequence, protocol, operation, request_content_type,
            request_encryption_key_id, request_payload_inline,
            request_payload_blob_provider, request_payload_blob_name,
            request_payload_blob_etag, request_payload_length,
            request_payload_nonce, request_payload_tag, request_payload_sha256,
            request_metadata::text, idempotency_key, created_at, deadline_at,
            state, attempt_count, lease_owner, lease_expires_at,
            response_content_type, response_encryption_key_id,
            response_payload_inline, response_payload_blob_provider,
            response_payload_blob_name, response_payload_blob_etag,
            response_payload_length, response_payload_nonce, response_payload_tag,
            response_payload_sha256, response_metadata::text, response_is_error,
            error_code, error_detail, completed_at
        FROM application_requests
        """;

    private const string ReturningColumns = """
        request.id, request.session_id, request.sequence, request.protocol,
        request.operation, request.request_content_type,
        request.request_encryption_key_id, request.request_payload_inline,
        request.request_payload_blob_provider, request.request_payload_blob_name,
        request.request_payload_blob_etag, request.request_payload_length,
        request.request_payload_nonce, request.request_payload_tag,
        request.request_payload_sha256, request.request_metadata::text,
        request.idempotency_key, request.created_at, request.deadline_at,
        request.state, request.attempt_count, request.lease_owner,
        request.lease_expires_at, request.response_content_type,
        request.response_encryption_key_id, request.response_payload_inline,
        request.response_payload_blob_provider, request.response_payload_blob_name,
        request.response_payload_blob_etag, request.response_payload_length,
        request.response_payload_nonce, request.response_payload_tag,
        request.response_payload_sha256, request.response_metadata::text,
        request.response_is_error, request.error_code, request.error_detail,
        request.completed_at
        """;
}
