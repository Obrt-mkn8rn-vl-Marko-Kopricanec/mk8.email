namespace mk8.email.Configuration;

public sealed class JmapConfig
{
    public int Port { get; init; } = 8081;
    public bool EnableJmap { get; init; } = true;
    public bool IsDefault { get; init; } = true;
    // The persisted JSON configuration encodes this URI as a string.
#pragma warning disable CA1056
    public string? PublicBaseUrl { get; init; }
#pragma warning restore CA1056
    public long MaxUploadSizeBytes { get; init; } = 50_000_000;
    public long MaxRequestSizeBytes { get; init; } = 10_000_000;
    public int MaxCallsInRequest { get; init; } = 64;
    public int MaxObjectsInGet { get; init; } = 500;
    public int MaxObjectsInSet { get; init; } = 500;
    public int MaxConcurrentRequests { get; init; } = 8;
    public int MaxConcurrentUploads { get; init; } = 4;
    public int UploadRetentionHours { get; init; } = 24;
    public long MaxUnreferencedBlobBytesPerAccount { get; init; } = 100_000_000;

    public Uri GetPublicBaseUri(string smtpHostname)
    {
        var value = string.IsNullOrWhiteSpace(PublicBaseUrl)
            ? $"https://{smtpHostname}"
            : PublicBaseUrl;
        return new Uri(value.TrimEnd('/') + "/", UriKind.Absolute);
    }
}
