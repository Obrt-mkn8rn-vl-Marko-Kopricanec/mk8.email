using System.Collections.Frozen;

namespace mk8.email.Contracts.Messaging;

public static class DistributedContractVersions
{
    // Bump when the two roles' internal contract interpretation changes.
    // This is compatibility metadata, not a release hash or peer attestation.
    public const string Current = "mk8.distributed.v5";

    public static IReadOnlySet<string> SupersededOperations { get; } = new[]
    {
        "jmap.api.process", "jmap.batch.execute", "jmap.batch.execute.v2",
        "jmap.batch.execute.v3", "jmap.session.get", "jmap.event.poll", "webpush.send",
    }.ToFrozenSet(StringComparer.Ordinal);
}
