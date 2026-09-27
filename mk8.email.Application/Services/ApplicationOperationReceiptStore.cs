using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class ApplicationOperationReceiptStore(
    EmailDbContext database,
    ILargeObjectStore objects,
    LargeObjectTransactionEffects transactionEffects,
    EnvironmentConfig environment,
    ILogger<ApplicationOperationReceiptStore> logger,
    IStoredContentProtector? protector = null,
    IDurablePresentationEffectSink? sink = null)
{
    public const string ContentType = "application/vnd.mk8.protected-receipt+json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    private sealed record StoredContent(int Version, string InputHash, ReadOnlyMemory<byte> Result, ApplicationRequest[] Effects);

    public async Task<ApplicationReceiptContent?> FindLockedAsync(ApplicationReceiptKey key, CancellationToken cancellationToken)
    {
        RequireTransaction(key);
        var lockKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(key.OperationId.ToByteArray()));
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken).ConfigureAwait(false);
        var row = await database.ApplicationOperationReceipts.AsNoTracking().SingleOrDefaultAsync(
            receipt => receipt.OperationId == key.OperationId && receipt.StepNumber == key.StepNumber,
            cancellationToken).ConfigureAwait(false);
        if (row is null)
            return null;
        if (row.UserId != key.UserId || !string.Equals(row.Purpose, key.Purpose, StringComparison.Ordinal))
            throw new InvalidOperationException("The operation receipt identity conflicts with the request.");
        var content = await ReadAsync(row, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(content.InputHash, key.InputHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The operation receipt input conflicts with the request.");
        return new ApplicationReceiptContent(content.Result, content.Effects);
    }

    public async Task SaveAsync(ApplicationReceiptKey key, ApplicationReceiptContent content, CancellationToken cancellationToken)
    {
        RequireTransaction(key);
        ArgumentNullException.ThrowIfNull(content);
        var protection = protector ?? throw new InvalidOperationException("Receipt content protection is required.");
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
            throw new InvalidOperationException("Operation receipts require Azure Blob protocol.");
        var payload = new StoredContent(1, key.InputHash, content.Result, content.Effects.ToArray());
        var envelope = protection.Protect(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions), AssociatedData(key));
        if (envelope.Length > MaximumEnvelopeLength())
            throw new InvalidOperationException("The operation receipt exceeds its storage limit.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(envelope.Span));
        var id = Guid.CreateVersion7();
        var name = $"application-receipts/{key.OperationId:N}/{id:N}/{hash}";
        var source = new MemoryStream(envelope.ToArray(), writable: false);
        await using var sourceLifetime = source.ConfigureAwait(false);
        var written = await objects.PutIfAbsentAsync(name, source, envelope.Length, hash, ContentType, cancellationToken).ConfigureAwait(false);
        if (written.Created)
            transactionEffects.DeleteOnRollback(written.Reference);
        ValidateReference(written.Reference, name, envelope.Length, hash);
        await database.ApplicationOperationReceipts.AddAsync(new ApplicationOperationReceiptDB
        {
            Id = id,
            OperationId = key.OperationId,
            StepNumber = key.StepNumber,
            UserId = key.UserId,
            Purpose = key.Purpose,
            ObjectProvider = written.Reference.Provider,
            ObjectName = written.Reference.ObjectName,
            ObjectSha256 = written.Reference.Sha256,
            ObjectEntityTag = written.Reference.EntityTag,
            PayloadLength = written.Reference.Length,
            CreatedAt = DateTime.UtcNow,
            EffectsPending = payload.Effects.Length != 0,
            EffectsRetryAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (payload.Effects.Length != 0)
            await NotifyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DispatchNextEffectsAsync(CancellationToken cancellationToken)
    {
        if (!database.Database.IsRelational())
            return false;
        if (database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Effect dispatch must occur outside the business transaction.");
        var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var row = await database.ApplicationOperationReceipts.FromSqlRaw("""
            SELECT * FROM application_operation_receipts
            WHERE effects_pending AND effects_retry_at <= clock_timestamp()
            ORDER BY created_at, id FOR UPDATE SKIP LOCKED LIMIT 1
            """).AsTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
            return false;
        try
        {
            var dispatcher = sink ?? throw new InvalidOperationException("The durable effect sink is unavailable.");
            var content = await ReadAsync(row, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            foreach (var effect in content.Effects)
                await dispatcher.EnqueueAsync(effect, timeout.Token).ConfigureAwait(false);
            row.EffectsPending = false;
        }
        // A failed enqueue must leave the committed effect retryable, never discard it.
#pragma warning disable CA1031
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
#pragma warning restore CA1031
            row.EffectsRetryAt = DateTime.UtcNow.AddSeconds(30);
            ReceiptEffectLog.DispatchFailed(logger, row.Id, exception);
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        database.Entry(row).State = EntityState.Detached;
        return true;
    }

    private async Task<StoredContent> ReadAsync(ApplicationOperationReceiptDB row, CancellationToken cancellationToken)
    {
        var protection = protector ?? throw new InvalidOperationException("Receipt content protection is required.");
        var maximum = MaximumEnvelopeLength();
        if (row.PayloadLength < 1 || row.PayloadLength > maximum)
            throw new InvalidOperationException("The operation receipt length is invalid.");
        var reference = new LargeObjectReference(row.ObjectProvider, row.ObjectName, row.PayloadLength, row.ObjectSha256, row.ObjectEntityTag);
        ValidateReference(reference, row.ObjectName, row.PayloadLength, row.ObjectSha256);
        var destination = new MemoryStream(checked((int)row.PayloadLength));
        await using var destinationLifetime = destination.ConfigureAwait(false);
        await objects.CopyToAsync(reference, destination, cancellationToken).ConfigureAwait(false);
        var envelope = destination.ToArray();
        if (envelope.LongLength != row.PayloadLength
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(envelope), Convert.FromHexString(row.ObjectSha256)))
            throw new InvalidOperationException("The operation receipt content changed.");
        var key = new ApplicationReceiptKey(row.OperationId, row.StepNumber, row.UserId, row.Purpose, string.Empty);
        var content = JsonSerializer.Deserialize<StoredContent>(protection.Unprotect(envelope, AssociatedData(key)).Span, JsonOptions);
        if (content is null || content.Version != 1 || content.InputHash is null || content.Effects is null)
            throw new InvalidOperationException("The operation receipt content is incomplete.");
        return content;
    }

    private void RequireTransaction(ApplicationReceiptKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.OperationId == Guid.Empty || key.UserId == Guid.Empty || key.StepNumber < 0
            || string.IsNullOrWhiteSpace(key.Purpose) || key.Purpose.Length > 64
            || key.InputHash is null || key.InputHash.Length != 64)
            throw new ArgumentException("An operation receipt identity is invalid.", nameof(key));
        if (!string.Equals(database.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal)
            || database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Operation receipts require the caller's PostgreSQL transaction.");
    }

    private long MaximumEnvelopeLength() => Math.Min(int.MaxValue, environment.Messaging.MaxPayloadBytes * 4L + 65_536);

    private Task<int> NotifyAsync(CancellationToken cancellationToken) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_notify('mk8_application_request', 'receipt-effects')", cancellationToken);

    private static byte[] AssociatedData(ApplicationReceiptKey key) => Encoding.UTF8.GetBytes(
        $"application.receipt.v1\0{key.OperationId:N}\0{key.StepNumber.ToString(CultureInfo.InvariantCulture)}\0{key.UserId:N}\0{key.Purpose}");

    private static void ValidateReference(LargeObjectReference reference, string name, long length, string hash)
    {
        if (!string.Equals(reference.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal)
            || !string.Equals(reference.ObjectName, name, StringComparison.Ordinal)
            || reference.Length != length || !string.Equals(reference.Sha256, hash, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(reference.EntityTag))
            throw new InvalidOperationException("The operation receipt Blob reference is invalid.");
    }
}
