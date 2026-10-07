using System.Collections.Frozen;

namespace mk8.email.Contracts.Messaging;

public static class DistributedContractVersions
{
    // Bump when the two roles' internal contract interpretation changes.
    // This is compatibility metadata, not a release hash or peer attestation.
    public const string Current = "mk8.distributed.v43";

    public static IReadOnlySet<string> SupersededOperations { get; } = new[]
    {
        "jmap.api.process", "jmap.batch.execute", "jmap.batch.execute.v2",
        "jmap.batch.execute.v3", "jmap.batch.execute.v4", "jmap.batch.execute.v5", "jmap.batch.execute.v6",
        "jmap.profile.get", "jmap.upload", "jmap.download", "jmap.changes.poll",
        "jmap.session.get", "jmap.event.poll", "webpush.send", "mail.operation.execute",
        "mail.operation.execute.v2", "mail.operation.execute.v3", "mail.operation.execute.v4",
        "mail.operation.execute.v5", "mail.operation.execute.v6", "mail.operation.execute.v7",
        "mail.operation.execute.v8", "mail.operation.execute.v9", "mail.operation.execute.v10",
        "mail.operation.execute.v11", "mail.operation.execute.v12", "mail.operation.execute.v13",
        "mail.operation.execute.v14", "mail.operation.execute.v15", "mail.operation.execute.v16",
        "mail.operation.execute.v17", "mail.operation.execute.v18", "mail.operation.execute.v19",
        "mail.operation.execute.v20", "mail.operation.execute.v21", "mail.operation.execute.v22",
        "mail.operation.execute.v23", "mail.operation.execute.v24", "mail.operation.execute.v25",
        "mail.operation.execute.v26", "mail.operation.execute.v27", "mail.operation.execute.v28", "mail.operation.execute.v29",
        "mail.operation.execute.v30", "mail.operation.execute.v31", "mail.operation.execute.v32", "mail.operation.execute.v33", "mail.operation.execute.v34", "mail.operation.execute.v35",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Every batch format that can create invocation receipts, including superseded formats.
    public static IReadOnlySet<string> ReceiptBatchOperations { get; } = new[]
    {
        "jmap.batch.execute.v4", "jmap.batch.execute.v5", "jmap.batch.execute.v6",
        "mail.operation.execute", "mail.operation.execute.v2", "mail.operation.execute.v3",
        "mail.operation.execute.v4", "mail.operation.execute.v5", "mail.operation.execute.v6",
        "mail.operation.execute.v7", "mail.operation.execute.v8", "mail.operation.execute.v9",
        "mail.operation.execute.v10", "mail.operation.execute.v11", "mail.operation.execute.v12",
        "mail.operation.execute.v13", "mail.operation.execute.v14", "mail.operation.execute.v15",
        "mail.operation.execute.v16", "mail.operation.execute.v17", "mail.operation.execute.v18",
        "mail.operation.execute.v19", "mail.operation.execute.v20", "mail.operation.execute.v21",
        "mail.operation.execute.v22", "mail.operation.execute.v23", "mail.operation.execute.v24",
        "mail.operation.execute.v25", "mail.operation.execute.v26", "mail.operation.execute.v27",
        "mail.operation.execute.v28", "mail.operation.execute.v29", "mail.operation.execute.v30", "mail.operation.execute.v31", "mail.operation.execute.v32", "mail.operation.execute.v33", "mail.operation.execute.v34", "mail.operation.execute.v35",
        ApplicationOperations.MailOperationExecute,
    }.ToFrozenSet(StringComparer.Ordinal);
}
