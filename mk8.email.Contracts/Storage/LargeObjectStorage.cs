namespace mk8.email.Contracts.Storage;

public static class LargeObjectProviders
{
    public const string AzureBlob = "azure-blob";
}

public sealed record LargeObjectReference(
    string Provider,
    string ObjectName,
    long Length,
    string Sha256,
    string EntityTag);

public sealed record LargeObjectWriteResult(
    LargeObjectReference Reference,
    bool Created);

public interface ILargeObjectStore
{
    string Provider { get; }

    Task<LargeObjectWriteResult> PutIfAbsentAsync(
        string objectName,
        Stream content,
        long length,
        string sha256,
        string contentType,
        CancellationToken cancellationToken = default);

    Task CopyToAsync(
        LargeObjectReference reference,
        Stream destination,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteIfMatchAsync(
        LargeObjectReference reference,
        CancellationToken cancellationToken = default);
}
