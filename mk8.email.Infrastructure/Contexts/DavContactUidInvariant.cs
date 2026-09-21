using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Data;

public static class DavContactUidInvariant
{
    public static async Task AcquireAccountLockAsync(
        EmailDbContext database,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                database.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            return;
        }
        if (database.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The contact UID account lock requires an active database transaction.");
        }

        var digest = SHA256.HashData(userId.ToByteArray());
        var key = BinaryPrimitives.ReadInt64BigEndian(digest);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({key})",
            cancellationToken);
    }

    public static Guid? ScopeFor(DavCollectionDB collection) =>
        collection.CollectionType == DavCollectionDB.AddressBookType
            ? collection.UserId
            : null;
}
