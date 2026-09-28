using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using mk8.email.Contracts.Storage;
using mk8.email.Messaging;
using Npgsql;

namespace mk8.email.Hosting;

/// <summary>
/// Checks the live encrypted-record snapshot before a role cutover or key retirement.
/// Archived snapshots must be checked separately before retiring their keys.
/// </summary>
public static class DistributedMessagingKeyAudit
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    public static async Task<long> AuditAsync(
        NpgsqlDataSource dataSource,
        ILargeObjectStore objects,
        IReadOnlyCollection<string> availableKeyIds,
        long maximumReceiptLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(availableKeyIds);
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal)
            || maximumReceiptLength < 1 || maximumReceiptLength > int.MaxValue)
            throw new ArgumentException("A bounded Azure Blob receipt store is required.", nameof(objects));
        var keys = availableKeyIds.ToHashSet(StringComparer.Ordinal);
        if (keys.Count != availableKeyIds.Count || keys.Count == 0 || keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("The configured messaging key identifiers are invalid.", nameof(availableKeyIds));

        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            await using var transactionLifetime = transaction.ConfigureAwait(false);
            var readOnly = connection.CreateCommand();
            await using (readOnly.ConfigureAwait(false))
            {
                readOnly.Transaction = transaction;
                readOnly.CommandText = "SET TRANSACTION READ ONLY";
                await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ValidateKeySchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var rows = await AuditIndexedRowsAsync(connection, transaction, keys, cancellationToken)
                .ConfigureAwait(false);
            var receipts = await AuditReceiptsAsync(
                connection, transaction, objects, keys, maximumReceiptLength, cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return checked(rows + receipts);
        }
    }

    private static async Task ValidateKeySchemaAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "gateway_traffic_records.encryption_key_id",
            "application_requests.request_encryption_key_id",
            "application_requests.response_encryption_key_id",
            "presentation_requests.request_encryption_key_id",
            "presentation_requests.response_encryption_key_id",
        };
        var found = new HashSet<string>(StringComparer.Ordinal);
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT table_name, column_name FROM information_schema.columns
            WHERE table_schema = current_schema() AND column_name LIKE '%encryption_key_id'
            """;
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerLifetime = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            found.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        if (!found.SetEquals(expected))
            throw new InvalidOperationException("The distributed messaging key-column schema changed; update the audit before key retirement.");
    }

    private static async Task<long> AuditIndexedRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HashSet<string> keys,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source, key_id, count(*)::bigint
            FROM (
                SELECT 'gateway traffic' AS source, encryption_key_id AS key_id FROM gateway_traffic_records
                UNION ALL SELECT 'application request', request_encryption_key_id FROM application_requests
                UNION ALL SELECT 'application response', response_encryption_key_id FROM application_requests WHERE response_encryption_key_id IS NOT NULL
                UNION ALL SELECT 'presentation request', request_encryption_key_id FROM presentation_requests
                UNION ALL SELECT 'presentation response', response_encryption_key_id FROM presentation_requests WHERE response_encryption_key_id IS NOT NULL
            ) AS encrypted
            GROUP BY source, key_id
            ORDER BY source, key_id
            """;
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerLifetime = reader.ConfigureAwait(false);
        long rows = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var source = reader.GetString(0);
            if (await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                || !keys.Contains(reader.GetString(1)))
                throw new InvalidOperationException($"A {source} encryption key is absent from the configured key ring.");
            rows = checked(rows + reader.GetInt64(2));
        }
        return rows;
    }

    private static async Task<long> AuditReceiptsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ILargeObjectStore objects,
        HashSet<string> keys, long maximumReceiptLength, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, payload_object_provider, payload_object_name, payload_object_sha256,
                   payload_object_etag, payload_length
            FROM application_operation_receipts ORDER BY id
            """;
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerLifetime = reader.ConfigureAwait(false);
        long receipts = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            var reference = new LargeObjectReference(
                reader.GetString(1), reader.GetString(2), reader.GetInt64(5),
                reader.GetString(3), reader.GetString(4));
            await AuditReceiptAsync(id, reference, objects, keys, maximumReceiptLength, cancellationToken)
                .ConfigureAwait(false);
            receipts++;
        }
        return receipts;
    }

    private static async Task AuditReceiptAsync(
        Guid id, LargeObjectReference reference, ILargeObjectStore objects,
        HashSet<string> keys, long maximumReceiptLength, CancellationToken cancellationToken)
    {
        if (!string.Equals(reference.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal)
            || reference.Length < 1 || reference.Length > maximumReceiptLength
            || reference.Sha256.Length != 64 || string.IsNullOrWhiteSpace(reference.EntityTag))
            throw new InvalidOperationException($"Operation receipt {id} has an invalid Blob reference.");
        var content = new MemoryStream(checked((int)reference.Length));
        await using var contentLifetime = content.ConfigureAwait(false);
        await objects.CopyToAsync(reference, content, cancellationToken).ConfigureAwait(false);
        var envelope = content.ToArray();
        if (envelope.LongLength != reference.Length
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(envelope), Convert.FromHexString(reference.Sha256)))
            throw new InvalidOperationException($"Operation receipt {id} changed in Blob storage.");
        var payload = JsonSerializer.Deserialize<ProtectedPayload>(envelope, JsonOptions);
        if (payload is null || string.IsNullOrWhiteSpace(payload.KeyId)
            || payload.Ciphertext is null || payload.Nonce is not { Length: 12 }
            || payload.Tag is not { Length: 16 })
            throw new InvalidOperationException($"Operation receipt {id} has an invalid protected envelope.");
        if (!keys.Contains(payload.KeyId))
            throw new InvalidOperationException($"Operation receipt {id} requires an unavailable messaging key.");
    }
}
