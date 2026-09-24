namespace mk8.email.Hosting;

public sealed record DistributedBackupRestoreResult(
    long ReferenceCount,
    long ImportedObjectCount);
