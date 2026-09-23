using mk8.email.Messaging;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
public sealed class Pop3MaildropLeaseTests
{
    [TestMethod]
    public async Task GatewayHostsShareOneRecoverableMaildropLease()
    {
        await using var database = await RequirePostgresAsync();
        await using var firstSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var secondSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(firstSource);
        var firstHost = new PostgresPop3MaildropLeaseStore(firstSource);
        var secondHost = new PostgresPop3MaildropLeaseStore(secondSource);
        var userId = Guid.CreateVersion7();
        var lifetime = TimeSpan.FromMinutes(2);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            (index % 2 == 0 ? firstHost : secondHost).TryAcquireAsync(userId, lifetime)));
        var acquired = attempts.OfType<Pop3MaildropLease>().ToArray();
        Assert.HasCount(1, acquired);
        var original = acquired[0];
        Assert.AreEqual(userId, original.UserId);
        Assert.IsNull(await secondHost.TryAcquireAsync(userId, lifetime));

        await secondHost.ReleaseAsync(new Pop3MaildropLease(userId, Guid.CreateVersion7()));
        Assert.IsTrue(await firstHost.RenewAsync(original, lifetime));
        Assert.IsNull(await secondHost.TryAcquireAsync(userId, lifetime));

        await using (var connection = await firstSource.OpenConnectionAsync())
        await using (var expire = connection.CreateCommand())
        {
            expire.CommandText =
                """
                UPDATE pop3_maildrop_leases
                   SET expires_at = clock_timestamp() - interval '1 second'
                 WHERE user_id = @user_id
                """;
            expire.Parameters.AddWithValue("user_id", userId);
            Assert.AreEqual(1, await expire.ExecuteNonQueryAsync());
        }

        var replacement = await secondHost.TryAcquireAsync(userId, lifetime);
        Assert.IsNotNull(replacement);
        Assert.AreNotEqual(original.OwnerToken, replacement.OwnerToken);
        Assert.IsFalse(await firstHost.RenewAsync(original, lifetime));
        await firstHost.ReleaseAsync(original);
        Assert.IsTrue(await secondHost.RenewAsync(replacement, lifetime));
        await secondHost.ReleaseAsync(replacement);
        Assert.IsNotNull(await firstHost.TryAcquireAsync(userId, lifetime));
    }

    [TestMethod]
    public async Task DistinctUsersHaveIndependentLeases()
    {
        await using var database = await RequirePostgresAsync();
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(source);
        var store = new PostgresPop3MaildropLeaseStore(source);
        var first = await store.TryAcquireAsync(Guid.CreateVersion7(), TimeSpan.FromMinutes(1));
        var second = await store.TryAcquireAsync(Guid.CreateVersion7(), TimeSpan.FromMinutes(1));
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        await store.ReleaseAsync(first);
        await store.ReleaseAsync(second);
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }
}
