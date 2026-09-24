namespace mk8.email.Hosting;

public sealed record DistributedBackupExportResult(
    long ReferenceCount,
    long UniqueContentCount,
    string DatabaseSha256,
    string ManifestSha256);
