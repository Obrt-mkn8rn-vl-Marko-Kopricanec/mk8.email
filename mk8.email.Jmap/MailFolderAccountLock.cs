using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal static class MailFolderAccountLock
{
    internal static bool UsesPostgreSql(EmailDbContext database) =>
        string.Equals(database.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);

    public static async Task AcquireAsync(EmailDbContext database, Guid accountId, CancellationToken cancellationToken)
    {
        if (!UsesPostgreSql(database)) return;
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Folder operations require the existing atomic Worker transaction.");
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM public.inboxes WHERE id = {accountId} FOR NO KEY UPDATE",
            cancellationToken).ConfigureAwait(false);
    }
}
