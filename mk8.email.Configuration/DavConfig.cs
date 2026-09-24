namespace mk8.email.Configuration;

public sealed class DavConfig
{
    public bool EnableDav { get; init; } = true;
    public int MaxResourceSizeBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxCollectionsPerUser { get; init; } = 100;
    public int MaxResourcesPerCollection { get; init; } = 100_000;
}
