using System.Collections.Frozen;

namespace mk8.email.Contracts.Messaging;

public static class DistributedContractVersions
{
    // Bump when the two roles' internal contract interpretation changes.
    // This is compatibility metadata, not a release hash or peer attestation.
    public const string Current = "mk8.distributed.v8";

    public static IReadOnlySet<string> SupersededOperations { get; } = new[]
    {
        "jmap.api.process", "jmap.batch.execute", "jmap.batch.execute.v2",
        "jmap.batch.execute.v3", "jmap.batch.execute.v4", "jmap.batch.execute.v5", "jmap.batch.execute.v6",
        "jmap.profile.get", "jmap.upload", "jmap.download", "jmap.changes.poll",
        "jmap.session.get", "jmap.event.poll", "webpush.send",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Every batch format that can create invocation receipts, including superseded formats.
    public static IReadOnlySet<string> ReceiptBatchOperations { get; } = new[]
    {
        "jmap.batch.execute.v4", "jmap.batch.execute.v5", "jmap.batch.execute.v6", ApplicationOperations.MailOperationExecute,
    }.ToFrozenSet(StringComparer.Ordinal);
}
