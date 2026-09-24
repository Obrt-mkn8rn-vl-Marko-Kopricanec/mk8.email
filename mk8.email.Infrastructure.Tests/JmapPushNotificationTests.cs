using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using Npgsql;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability", "CA1515",
    Justification = "MSTest discovers this public test class by reflection.")]
public sealed class JmapPushNotificationTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Maintainability", "MA0051",
        Justification = "The notification transaction scenario keeps setup and assertions together.")]
    public async Task RuntimeSchemaNotifiesVerifiedSubscriptionsAndCommittedStateChanges()
    {
        var server = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
        if (server is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }
        await using var serverLifetime = server.ConfigureAwait(false);
        {
            var database = server.CreateContext();
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
        }

        var userId = Guid.CreateVersion7();
        var subscriptionId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var listener = new NpgsqlConnection(server.ConnectionString);
        await using var listenerLifetime = listener.ConfigureAwait(false);
        await listener.OpenAsync().ConfigureAwait(false);
        string? notification = null;
        listener.Notification += (_, args) => notification = args.Payload;
        {
            var listen = listener.CreateCommand();
            await using var listenLifetime = listen.ConfigureAwait(false);
            listen.CommandText = "LISTEN mk8_jmap_push_ready";
            await listen.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        {
            var writer = new NpgsqlConnection(server.ConnectionString);
            await using var writerLifetime = writer.ConfigureAwait(false);
            await writer.OpenAsync().ConfigureAwait(false);
            {
                var createUser = writer.CreateCommand();
                await using var createUserLifetime = createUser.ConfigureAwait(false);
                createUser.CommandText =
                    "INSERT INTO users "
                    + "(id, username, password_hash, role, quota_bytes, is_active, created_at, updated_at) "
                    + "VALUES (@id, 'push-notify@example.test', 'unused', 'User', 0, true, @now, @now)";
                createUser.Parameters.AddWithValue("id", userId);
                createUser.Parameters.AddWithValue("now", DateTime.UtcNow);
                await createUser.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            {
                var createSubscription = writer.CreateCommand();
                await using var createSubscriptionLifetime = createSubscription.ConfigureAwait(false);
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
                await createSubscription.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            Assert.IsFalse(await listener.WaitAsync(100).ConfigureAwait(false));

            {
                var verify = writer.CreateCommand();
                await using var verifyLifetime = verify.ConfigureAwait(false);
                verify.CommandText =
                    "UPDATE jmap_push_subscriptions SET is_verified = true WHERE id = @id";
                verify.Parameters.AddWithValue("id", subscriptionId);
                await verify.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            Assert.IsTrue(await listener.WaitAsync(5_000).ConfigureAwait(false));
            Assert.AreEqual(userId.ToString(), notification, StringComparer.Ordinal);

            {
                var change = writer.CreateCommand();
                await using var changeLifetime = change.ConfigureAwait(false);
                change.CommandText =
                    "INSERT INTO jmap_changes "
                    + "(account_id, data_type, object_id, change_kind, changed_at) "
                    + "VALUES (@account_id, 'Email', 'message', 'created', @now)";
                change.Parameters.AddWithValue("account_id", accountId);
                change.Parameters.AddWithValue("now", DateTime.UtcNow);
                await change.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            Assert.IsTrue(await listener.WaitAsync(5_000).ConfigureAwait(false));
            Assert.AreEqual(accountId.ToString(), notification, StringComparer.Ordinal);
        }
    }
}
