using System.Security.Cryptography;
using System.Text.Json;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class VacationResponseContentService(
    ILargeObjectStore objects,
    LargeObjectTransactionEffects transactionEffects)
{
    public const string ContentType = "application/vnd.mk8.vacation-bodies+json";

    public async Task<(string? TextBody, string? HtmlBody)> ReadAsync(
        JmapVacationResponseDB response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        EnsureAzureBlobProvider();
        var reference = GetReference(response);
        if (response.TextBody is not null || response.HtmlBody is not null)
        {
            if (reference is not null)
                throw new InvalidOperationException("A vacation response has both inline and external bodies.");
            return (response.TextBody, response.HtmlBody);
        }
        if (reference is null)
            return default;

        var destination = new MemoryStream();
        await using (destination.ConfigureAwait(false))
        {
            await objects.CopyToAsync(reference, destination, cancellationToken).ConfigureAwait(false);
            var bytes = destination.ToArray();
            if (bytes.LongLength != reference.Length
                || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(bytes),
                    Convert.FromHexString(reference.Sha256)))
            {
                throw new InvalidOperationException(
                    $"Vacation response {response.AccountId:D} failed its content integrity check.");
            }
            var payload = JsonSerializer.Deserialize<VacationBodyPayload>(bytes)
                ?? throw new InvalidOperationException("A vacation response body object is invalid.");
            return (payload.TextBody, payload.HtmlBody);
        }
    }

    public async Task SetAsync(
        JmapVacationResponseDB response,
        string? textBody,
        string? htmlBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        EnsureAzureBlobProvider();
        if (response.AccountId == Guid.Empty)
            throw new InvalidOperationException("The vacation response account identifier is not valid.");

        var previous = GetReference(response);
        if (textBody is null && htmlBody is null)
        {
            ClearReference(response);
        }
        else
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new VacationBodyPayload(textBody, htmlBody));
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var source = new MemoryStream(bytes, writable: false);
            await using (source.ConfigureAwait(false))
            {
                // A unique write key prevents one concurrent update from deleting
                // another update's committed object during rollback.
                var written = await objects.PutIfAbsentAsync(
                    $"vacation/responses/{response.AccountId:N}/{hash}/{Guid.CreateVersion7():N}",
                    source,
                    bytes.LongLength,
                    hash,
                    ContentType,
                    cancellationToken).ConfigureAwait(false);
                response.BodySizeBytes = checked((int)written.Reference.Length);
                response.BodyObjectProvider = written.Reference.Provider;
                response.BodyObjectName = written.Reference.ObjectName;
                response.BodyObjectSha256 = written.Reference.Sha256;
                response.BodyObjectEntityTag = written.Reference.EntityTag;
                if (written.Created)
                    transactionEffects.DeleteOnRollback(written.Reference);
            }
        }

        response.TextBody = null;
        response.HtmlBody = null;
        if (previous is not null)
            transactionEffects.DeleteOnCommit(previous);
    }

    private static LargeObjectReference? GetReference(JmapVacationResponseDB response)
    {
        if (response.BodyObjectProvider is null
            && response.BodyObjectName is null
            && response.BodyObjectSha256 is null
            && response.BodyObjectEntityTag is null)
        {
            if (response.BodySizeBytes != 0)
                throw new InvalidOperationException("A vacation response has an incomplete body reference.");
            return null;
        }
        if (response.BodyObjectProvider is not { } provider
            || !string.Equals(provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(response.BodyObjectName)
            || string.IsNullOrWhiteSpace(response.BodyObjectSha256)
            || string.IsNullOrWhiteSpace(response.BodyObjectEntityTag)
            || response.BodySizeBytes <= 0)
        {
            throw new InvalidOperationException("A vacation response has an invalid Azure Blob reference.");
        }
        return new LargeObjectReference(
            provider,
            response.BodyObjectName,
            response.BodySizeBytes,
            response.BodyObjectSha256,
            response.BodyObjectEntityTag);
    }

    private static void ClearReference(JmapVacationResponseDB response)
    {
        response.BodySizeBytes = 0;
        response.BodyObjectProvider = null;
        response.BodyObjectName = null;
        response.BodyObjectSha256 = null;
        response.BodyObjectEntityTag = null;
    }

    private void EnsureAzureBlobProvider()
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
            throw new InvalidOperationException("Vacation responses require an Azure Blob-compatible object store.");
    }

    private sealed record VacationBodyPayload(string? TextBody, string? HtmlBody);
}
