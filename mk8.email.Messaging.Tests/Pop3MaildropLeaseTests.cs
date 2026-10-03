using mk8.email.Messaging;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class Pop3MaildropLeaseTests
{
    [TestMethod]
    public async Task GatewayHostsShareOneRecoverableMaildropLease()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var firstSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var firstSourceLifetime = firstSource.ConfigureAwait(false);
        var secondSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var secondSourceLifetime = secondSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(firstSource).ConfigureAwait(false);
        var firstHost = new PostgresPop3MaildropLeaseStore(firstSource);
        var secondHost = new PostgresPop3MaildropLeaseStore(secondSource);
        var userId = Guid.CreateVersion7();
        var lifetime = TimeSpan.FromMinutes(2);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            (index % 2 == 0 ? firstHost : secondHost).TryAcquireAsync(userId, lifetime))).ConfigureAwait(false);
        var acquired = attempts.OfType<Pop3MaildropLease>().ToArray();
        Assert.HasCount(1, acquired);
        var original = acquired[0];
        Assert.AreEqual(userId, original.UserId);
        Assert.IsNull(await secondHost.TryAcquireAsync(userId, lifetime).ConfigureAwait(false));

        await secondHost.ReleaseAsync(new Pop3MaildropLease(userId, Guid.CreateVersion7())).ConfigureAwait(false);
        Assert.IsTrue(await firstHost.RenewAsync(original, lifetime).ConfigureAwait(false));
        Assert.IsNull(await secondHost.TryAcquireAsync(userId, lifetime).ConfigureAwait(false));

        {
            var connection = (await firstSource.OpenConnectionAsync().ConfigureAwait(false));
            await using var connectionLifetime = connection.ConfigureAwait(false);
            var expire = connection.CreateCommand();
            await using var expireLifetime = expire.ConfigureAwait(false);
            expire.CommandText =
                """
                UPDATE pop3_maildrop_leases
                   SET expires_at = clock_timestamp() - interval '1 second'
                 WHERE user_id = @user_id
                """;
            expire.Parameters.AddWithValue("user_id", userId);
            Assert.AreEqual(1, await expire.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        var replacement = await secondHost.TryAcquireAsync(userId, lifetime).ConfigureAwait(false);
        Assert.IsNotNull(replacement);
        Assert.AreNotEqual(original.OwnerToken, replacement.OwnerToken);
        Assert.IsFalse(await firstHost.RenewAsync(original, lifetime).ConfigureAwait(false));
        await firstHost.ReleaseAsync(original).ConfigureAwait(false);
        Assert.IsTrue(await secondHost.RenewAsync(replacement, lifetime).ConfigureAwait(false));
        await secondHost.ReleaseAsync(replacement).ConfigureAwait(false);
        Assert.IsNotNull(await firstHost.TryAcquireAsync(userId, lifetime).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DistinctUsersHaveIndependentLeases()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var source = NpgsqlDataSource.Create(database.ConnectionString);
        await using var sourceLifetime = source.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(source).ConfigureAwait(false);
        var store = new PostgresPop3MaildropLeaseStore(source);
        var first = await store.TryAcquireAsync(Guid.CreateVersion7(), TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        var second = await store.TryAcquireAsync(Guid.CreateVersion7(), TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        await store.ReleaseAsync(first).ConfigureAwait(false);
        await store.ReleaseAsync(second).ConfigureAwait(false);
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
        if (database is null)
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }
}
