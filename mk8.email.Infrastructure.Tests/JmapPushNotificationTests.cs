using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using Npgsql;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class JmapPushNotificationTests
{
    [TestMethod]
    public async Task RuntimeSchemaNotifiesVerifiedSubscriptionsAndCommittedStateChanges()
    {
        await using var server = await PostgresTestDatabase.TryCreateAsync();
        if (server is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }
        await using (var database = server.CreateContext())
        {
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
        }

        var userId = Guid.CreateVersion7();
        var subscriptionId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        await using var listener = new NpgsqlConnection(server.ConnectionString);
        await listener.OpenAsync();
        string? notification = null;
        listener.Notification += (_, args) => notification = args.Payload;
        await using (var listen = listener.CreateCommand())
        {
            listen.CommandText = "LISTEN mk8_jmap_push_ready";
            await listen.ExecuteNonQueryAsync();
        }

        await using (var writer = new NpgsqlConnection(server.ConnectionString))
        {
            await writer.OpenAsync();
            await using (var createUser = writer.CreateCommand())
            {
                createUser.CommandText =
                    "INSERT INTO users "
                    + "(id, username, password_hash, role, quota_bytes, is_active, created_at, updated_at) "
                    + "VALUES (@id, 'push-notify@example.test', 'unused', 'User', 0, true, @now, @now)";
                createUser.Parameters.AddWithValue("id", userId);
                createUser.Parameters.AddWithValue("now", DateTime.UtcNow);
                await createUser.ExecuteNonQueryAsync();
            }
            await using (var createSubscription = writer.CreateCommand())
            {
                createSubscription.CommandText =
                    "INSERT INTO jmap_push_subscriptions "
                    + "(id, subscription_object_id, user_id, device_client_id, url, "
                    + "verification_code, is_verified, expires_at, created_at, updated_at, "
                    + "last_pushed_change, failure_count) "
                    + "VALUES (@id, 'push-notify', @user_id, 'device', "
                    + "'https://push.example.test/endpoint', 'code', false, @expires_at, @now, @now, 0, 0)";
                createSubscription.Parameters.AddWithValue("id", subscriptionId);
                createSubscription.Parameters.AddWithValue("user_id", userId);
                createSubscription.Parameters.AddWithValue("expires_at", DateTime.UtcNow.AddHours(1));
                createSubscription.Parameters.AddWithValue("now", DateTime.UtcNow);
                await createSubscription.ExecuteNonQueryAsync();
            }
            Assert.IsFalse(await listener.WaitAsync(100));

            await using (var verify = writer.CreateCommand())
            {
                verify.CommandText =
                    "UPDATE jmap_push_subscriptions SET is_verified = true WHERE id = @id";
                verify.Parameters.AddWithValue("id", subscriptionId);
                await verify.ExecuteNonQueryAsync();
            }
            Assert.IsTrue(await listener.WaitAsync(5_000));
            Assert.AreEqual(userId.ToString(), notification);

            await using (var change = writer.CreateCommand())
            {
                change.CommandText =
                    "INSERT INTO jmap_changes "
                    + "(account_id, data_type, object_id, change_kind, changed_at) "
                    + "VALUES (@account_id, 'Email', 'message', 'created', @now)";
                change.Parameters.AddWithValue("account_id", accountId);
                change.Parameters.AddWithValue("now", DateTime.UtcNow);
                await change.ExecuteNonQueryAsync();
            }
            Assert.IsTrue(await listener.WaitAsync(5_000));
            Assert.AreEqual(accountId.ToString(), notification);
        }
    }
}
