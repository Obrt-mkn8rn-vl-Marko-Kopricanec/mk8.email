namespace mk8.email.Hosting;

public sealed record DistributedBackupArchiveSummary(
    int SchemaVersion,
    long ReferenceCount,
    long UniqueContentCount,
    string DatabaseSha256,
    string ManifestSha256);
