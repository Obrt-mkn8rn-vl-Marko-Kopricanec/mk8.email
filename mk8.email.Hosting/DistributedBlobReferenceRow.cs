using mk8.email.Contracts.Storage;

namespace mk8.email.Hosting;

public sealed record DistributedBlobReferenceRow(
    string Source,
    Guid RowId,
    LargeObjectReference Reference,
    string ContentType);
