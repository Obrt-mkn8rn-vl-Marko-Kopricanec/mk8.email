using System.Runtime.CompilerServices;
using System.Text;
using mk8.email.Contracts.Storage;
using Npgsql;

namespace mk8.email.Hosting;

public sealed record DistributedBlobReferenceRow(
    string Source,
    Guid RowId,
    LargeObjectReference Reference,
    string ContentType);

/// <summary>
/// Enumerates every PostgreSQL-backed large-object reference in a distributed mail stack.
/// Backup and restore must use a single database snapshot and fail on schema drift.
/// </summary>
public static class DistributedBlobReferenceInventory
{
    internal sealed record Source(
        string Table,
        string Name,
        string Provider,
        string Sha256,
        string EntityTag,
        string Length,
        string ContentTypeSql)
    {
        public string Key => $"{Table}.{Name}";
    }

    internal static readonly Source[] Sources =
    [
        new("gateway_traffic_records", "payload_blob_name", "payload_blob_provider",
            "payload_sha256", "payload_blob_etag", "payload_length",
            "'application/vnd.mk8.encrypted-payload'"),
        new("application_requests", "request_payload_blob_name",
            "request_payload_blob_provider", "request_payload_sha256",
            "request_payload_blob_etag", "request_payload_length",
            "'application/vnd.mk8.encrypted-payload'"),
        new("application_requests", "response_payload_blob_name",
            "response_payload_blob_provider", "response_payload_sha256",
            "response_payload_blob_etag", "response_payload_length",
            "'application/vnd.mk8.encrypted-payload'"),
        new("presentation_requests", "request_payload_blob_name",
            "request_payload_blob_provider", "request_payload_sha256",
            "request_payload_blob_etag", "request_payload_length",
            "'application/vnd.mk8.encrypted-payload'"),
        new("presentation_requests", "response_payload_blob_name",
            "response_payload_blob_provider", "response_payload_sha256",
            "response_payload_blob_etag", "response_payload_length",
            "'application/vnd.mk8.encrypted-payload'"),
        new("emails", "raw_message_object_name", "raw_message_object_provider",
            "raw_message_object_sha256", "raw_message_object_etag", "size_bytes",
            "'message/rfc822'"),
        new("mail_queue_messages", "raw_message_object_name",
            "raw_message_object_provider", "raw_message_object_sha256",
            "raw_message_object_etag", "raw_message_size_bytes", "'message/rfc822'"),
        new("sieve_scripts", "object_name", "object_provider", "object_sha256",
            "object_etag", "size_bytes", "'application/sieve'"),
        new("dav_resources", "object_name", "object_provider", "object_sha256",
            "object_etag", "size_bytes", "content_type"),
        new("jmap_blobs", "object_name", "object_provider", "object_sha256",
            "object_etag", "size_bytes", "content_type"),
    ];

    public static async Task ValidateSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var columns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT table_name, column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(0);
                if (!columns.TryGetValue(table, out var names))
                {
                    names = new HashSet<string>(StringComparer.Ordinal);
                    columns.Add(table, names);
                }
                names.Add(reader.GetString(1));
            }
        }

        var expected = Sources.Select(source => source.Key)
            .ToHashSet(StringComparer.Ordinal);
        var actual = columns.SelectMany(table => table.Value
                .Where(name => name.EndsWith("object_name", StringComparison.Ordinal)
                    || name.EndsWith("blob_name", StringComparison.Ordinal))
                .Select(name => $"{table.Key}.{name}"))
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            var missing = string.Join(", ", expected.Except(actual).Order(StringComparer.Ordinal));
            var unknown = string.Join(", ", actual.Except(expected).Order(StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"Large-object reference schema drift (missing: {missing}; unknown: {unknown}).");
        }

        foreach (var source in Sources)
        {
            var names = columns[source.Table];
            foreach (var column in new[]
                     {
                         "id", source.Provider, source.Sha256, source.EntityTag, source.Length,
                     })
            {
                if (!names.Contains(column))
                    throw new InvalidOperationException(
                        $"Large-object reference schema drift: {source.Table}.{column} is missing.");
            }
            if (!source.ContentTypeSql.StartsWith('\'')
                && !names.Contains(source.ContentTypeSql))
            {
                throw new InvalidOperationException(
                    $"Large-object reference schema drift: "
                    + $"{source.Table}.{source.ContentTypeSql} is missing.");
            }
        }
    }

    internal static string BuildQuerySql()
    {
        var sql = new StringBuilder();
        foreach (var source in Sources)
        {
            if (sql.Length != 0)
                sql.AppendLine("UNION ALL");
            sql.Append("SELECT '").Append(source.Key).Append("' AS source, id, ")
                .Append(source.Provider).Append(", ").Append(source.Name).Append(", ")
                .Append(source.Length).Append("::bigint, ").Append(source.Sha256)
                .Append(", ").Append(source.EntityTag).Append(", ")
                .Append(source.ContentTypeSql).Append(" FROM ")
                .Append(source.Table).Append(" WHERE ")
                .Append(source.Provider).Append(" IS NOT NULL OR ")
                .Append(source.Name).Append(" IS NOT NULL OR ")
                .Append(source.Sha256).Append(" IS NOT NULL OR ")
                .Append(source.EntityTag).AppendLine(" IS NOT NULL");
        }
        return sql.ToString();
    }

    public static async IAsyncEnumerable<DistributedBlobReferenceRow> EnumerateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        await ValidateSchemaAsync(connection, transaction, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildQuerySql();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var source = reader.GetString(0);
            var rowId = reader.GetGuid(1);
            if (reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4)
                || reader.IsDBNull(5) || reader.IsDBNull(6))
            {
                throw new InvalidOperationException(
                    $"Incomplete large-object reference at {source}, row {rowId}.");
            }

            var reference = new LargeObjectReference(
                reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                reader.GetString(5), reader.GetString(6));
            if (reference.Provider != LargeObjectProviders.AzureBlob
                || reference.Length < 0
                || reference.Sha256.Length != 64
                || reference.Sha256.Any(character => character is not
                    (>= '0' and <= '9' or >= 'a' and <= 'f')))
            {
                throw new InvalidOperationException(
                    $"Invalid large-object reference at {source}, row {rowId}.");
            }
            if (reader.IsDBNull(7))
                throw new InvalidOperationException(
                    $"Missing large-object content type at {source}, row {rowId}.");
            var contentType = reader.GetString(7);
            if (string.IsNullOrWhiteSpace(contentType)
                || contentType.Length > 255
                || contentType.Any(char.IsControl))
            {
                throw new InvalidOperationException(
                    $"Invalid large-object content type at {source}, row {rowId}.");
            }
            yield return new DistributedBlobReferenceRow(source, rowId, reference, contentType);
        }
    }
}
