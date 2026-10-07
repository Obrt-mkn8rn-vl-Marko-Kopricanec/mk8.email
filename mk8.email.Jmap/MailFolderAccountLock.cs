using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal static class MailFolderAccountLock
{
    internal static bool UsesPostgreSql(EmailDbContext database) =>
        string.Equals(database.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);

    public static async Task AcquireAsync(EmailDbContext database, Guid accountId, CancellationToken cancellationToken)
    {
        await AcquireGateAsync(database, cancellationToken).ConfigureAwait(false);
        if (!UsesPostgreSql(database)) return;
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM public.inboxes WHERE id = {accountId} FOR NO KEY UPDATE",
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task AcquireGateAsync(EmailDbContext database, CancellationToken cancellationToken)
    {
        if (!UsesPostgreSql(database)) return;
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Folder operations require the existing atomic Worker transaction.");
        // Two-key MK8F:1 namespace, distinct from the existing bigint Blob/DAV locks.
        // Match the BEFORE STATEMENT guards and EmailDbContext's save boundary.
        await database.Database.ExecuteSqlRawAsync(
            "SELECT pg_catalog.pg_advisory_xact_lock(1296775238, 1)",
            cancellationToken).ConfigureAwait(false);
    }
}
