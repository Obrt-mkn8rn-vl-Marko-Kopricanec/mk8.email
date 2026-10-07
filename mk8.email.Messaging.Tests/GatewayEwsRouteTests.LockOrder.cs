using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;
using Npgsql;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task SqlWriterCannotInvertTypedMutationLocksAndLegacyOrderReallyDeadlocks(bool delete, bool legacy)
    {
        var barrier = new FolderLockBarrier();
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, folderLockInterceptor: barrier).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var emailId = await SeedLockOrderEmailAsync(fixture).ConfigureAwait(false);
        Authenticate(fixture);
        var key = await ReadKeyAsync(fixture, FolderId(GatewayEwsFixtureDomain.GrandchildId)).ConfigureAwait(false);
        if (legacy)
        {
            var setup = Context(fixture);
            await using var setupLifetime = setup.ConfigureAwait(false);
            await setup.Database.ExecuteSqlRawAsync("ALTER TABLE public.emails DISABLE TRIGGER mk8_email_write_gate").ConfigureAwait(false);
        }
        var writer = new NpgsqlConnection(fixture.ApplicationConnection);
        await using var writerLifetime = writer.ConfigureAwait(false);
        await writer.OpenAsync().ConfigureAwait(false);
        var transaction = await writer.BeginTransactionAsync().ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var command = writer.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = "UPDATE public.emails SET is_read=true WHERE id=@id";
        command.Parameters.AddWithValue("id", emailId);
        barrier.Arm();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var mutation = RunLockOrderMutationAsync(fixture, key, delete, deadline.Token);
        try
        {
            var ownerPid = await barrier.Locked.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var saving = command.ExecuteNonQueryAsync(deadline.Token);
            await AssertBlockedWriterAsync(fixture, writer.ProcessID, ownerPid, legacy, deadline.Token).ConfigureAwait(false);
            Assert.IsFalse(saving.IsCompleted);
            barrier.Resume();
            var writerError = await FinishSqlWriterAsync(saving, transaction, delete, legacy).ConfigureAwait(false);
            var result = await mutation.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (legacy)
                AssertLegacyDeadlockResult(result, writerError, barrier);
            else
            {
                Assert.IsNull(writerError);
                AssertMutationSucceeded(result);
                Assert.IsFalse(barrier.ProviderErrors.Contains(PostgresErrorCodes.DeadlockDetected, StringComparer.Ordinal));
                await AssertLockOrderResultAsync(fixture, emailId, delete).ConfigureAwait(false);
            }
        }
        finally
        {
            barrier.Resume();
            await ObserveMutationCleanupAsync(mutation, deadline).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EfMessageSaveWaitsBeforeScopeCollectionAndDoesNotDeadlockFolderMutation(bool delete)
    {
        var barrier = new FolderLockBarrier();
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, folderLockInterceptor: barrier).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var emailId = await SeedLockOrderEmailAsync(fixture).ConfigureAwait(false);
        Authenticate(fixture);
        var key = await ReadKeyAsync(fixture, FolderId(GatewayEwsFixtureDomain.GrandchildId)).ConfigureAwait(false);
        var writer = Context(fixture);
        await using var writerLifetime = writer.ConfigureAwait(false);
        await writer.Database.OpenConnectionAsync().ConfigureAwait(false);
        var email = await writer.Emails.SingleAsync(item => item.Id == emailId).ConfigureAwait(false);
        email.IsRead = true;
        barrier.Arm();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var mutation = RunLockOrderMutationAsync(fixture, key, delete, deadline.Token);
        try
        {
            var ownerPid = await barrier.Locked.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var saving = writer.SaveChangesAsync(deadline.Token);
            var pid = ((NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID;
            await AssertBlockedWriterAsync(fixture, pid, ownerPid, legacy: false, deadline.Token).ConfigureAwait(false);
            Assert.AreEqual(0, writer.ChangeTracker.Entries<JmapChangeDB>().Count());
            barrier.Resume();
            AssertMutationSucceeded(await mutation.WaitAsync(deadline.Token).ConfigureAwait(false));
            if (delete)
            {
                // Deliberately observe the already-started overlapping writer, not a second save.
#pragma warning disable VSTHRD003
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => saving).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            else Assert.IsGreaterThan(0, await saving.ConfigureAwait(false));
            await AssertLockOrderResultAsync(fixture, emailId, delete).ConfigureAwait(false);
        }
        finally
        {
            barrier.Resume();
            await ObserveMutationCleanupAsync(mutation, deadline).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CancelledGateAcquisitionDisposesTheUnregisteredTransactionAndAllowsContextReuse()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var owner = Context(fixture);
        await using var ownerLifetime = owner.ConfigureAwait(false);
        var holding = await owner.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using var holdingLifetime = holding.ConfigureAwait(false);
        var blocked = Context(fixture);
        await using var blockedLifetime = blocked.ConfigureAwait(false);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<OperationCanceledException>(() => blocked.Database.BeginTransactionAsync(cancelled.Token)).ConfigureAwait(false);
        Assert.IsNull(blocked.Database.CurrentTransaction);
        await holding.CommitAsync().ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reused = await blocked.Database.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await using var reusedLifetime = reused.ConfigureAwait(false);
        await reused.CommitAsync(deadline.Token).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MessageDeltaScopeIsResolvedOnlyAfterAConcurrentFolderAccountMoveCommits()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var emailId = await SeedLockOrderEmailAsync(fixture).ConfigureAwait(false);
        var owner = Context(fixture);
        await using var ownerLifetime = owner.ConfigureAwait(false);
        var before = await owner.JmapChanges.MaxAsync(item => item.Sequence).ConfigureAwait(false);
        var transaction = await owner.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        await owner.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.folders SET inbox_id={GatewayEwsFixtureDomain.ForeignAccountId} WHERE id={GatewayEwsFixtureDomain.GrandchildId}").ConfigureAwait(false);
        var writer = Context(fixture);
        await using var writerLifetime = writer.ConfigureAwait(false);
        await writer.Database.OpenConnectionAsync().ConfigureAwait(false);
        (await writer.Emails.SingleAsync(item => item.Id == emailId).ConfigureAwait(false)).IsRead = true;
        var saving = writer.SaveChangesAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await AssertBlockedWriterAsync(fixture, ((NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID,
            ((NpgsqlConnection)owner.Database.GetDbConnection()).ProcessID, legacy: false, deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(0, writer.ChangeTracker.Entries<JmapChangeDB>().Count());
        await transaction.CommitAsync().ConfigureAwait(false);
        await saving.WaitAsync(deadline.Token).ConfigureAwait(false);
        var changes = await owner.JmapChanges.AsNoTracking().Where(item => item.Sequence > before).ToListAsync().ConfigureAwait(false);
        Assert.IsNotEmpty(changes);
        Assert.IsTrue(changes.All(item => item.AccountId == GatewayEwsFixtureDomain.ForeignAccountId));
    }

    [TestMethod]
    [DataRow("mk8_email_write_gate")]
    [DataRow("mk8_inbox_write_gate")]
    [DataRow("mk8_user_delete_gate")]
    public async Task StatementGuardRepairIsAdditiveAndIdempotent(string name)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var database = Context(fixture);
        await using var lifetime = database.ConfigureAwait(false);
        var counts = (await database.Folders.CountAsync().ConfigureAwait(false), await database.Emails.CountAsync().ConfigureAwait(false));
        var sql = name switch
        {
            "mk8_email_write_gate" => "ALTER TABLE public.emails DISABLE TRIGGER mk8_email_write_gate",
            "mk8_inbox_write_gate" => "ALTER TABLE public.inboxes DISABLE TRIGGER mk8_inbox_write_gate",
            _ => "ALTER TABLE public.users DISABLE TRIGGER mk8_user_delete_gate",
        };
        await database.Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
        await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
        await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
        Assert.AreEqual(counts, (await database.Folders.CountAsync().ConfigureAwait(false), await database.Emails.CountAsync().ConfigureAwait(false)));
        Assert.AreEqual(6, await database.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM pg_trigger WHERE tgfoid=to_regprocedure('public.mk8_gate_mail_writes()') AND tgenabled='O' AND tgtype IN (10,30)").SingleAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SynchronousEfTransactionAndSaveRetainTheSameCoordinationGate()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await Task.Run(() =>
        {
            using var database = Context(fixture);
            using var transaction = database.Database.BeginTransaction();
            using var command = database.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "SELECT count(*) FROM pg_locks WHERE pid=pg_backend_pid() AND locktype='advisory' AND classid=1296775238 AND objid=1 AND objsubid=2 AND granted";
            Assert.AreEqual(1L, (long)command.ExecuteScalar()!);
            database.Folders.Add(new FolderDB { Id = Guid.CreateVersion7(), InboxId = GatewayEwsFixtureDomain.AccountId, Name = "SyncGate" });
            Assert.IsGreaterThan(0, database.SaveChanges());
            transaction.Commit();
        }).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("inboxes")]
    [DataRow("addresses")]
    [DataRow("users")]
    [DataRow("companies")]
    public async Task ParentCascadeWaitsBeforeLocksThatCouldInvertTypedMutation(string parent)
    {
        var barrier = new FolderLockBarrier();
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, folderLockInterceptor: barrier).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var key = await ReadKeyAsync(fixture, FolderId(GatewayEwsFixtureDomain.GrandchildId)).ConfigureAwait(false);
        var writer = new NpgsqlConnection(fixture.ApplicationConnection);
        await using var writerLifetime = writer.ConfigureAwait(false);
        await writer.OpenAsync().ConfigureAwait(false);
        var command = writer.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        SetParentCascadeCommand(command, parent);
        command.Parameters.AddWithValue("id", GatewayEwsFixtureDomain.AccountId);
        command.Parameters.AddWithValue("owner", GatewayEwsFixtureDomain.OwnerId);
        barrier.Arm();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var mutation = RunParentLockOrderMutationAsync(fixture, key, deadline.Token);
        try
        {
            var ownerPid = await barrier.Locked.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var deleting = command.ExecuteNonQueryAsync(deadline.Token);
            await AssertBlockedWriterAsync(fixture, writer.ProcessID, ownerPid, legacy: false, deadline.Token).ConfigureAwait(false);
            barrier.Resume();
            var updated = await mutation.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.AreEqual(MailFolderMutationStatus.Ok, updated.Status);
            Assert.AreEqual(1, updated.Updated.Count);
            Assert.IsNull(updated.Updated[0].Failure);
            Assert.AreEqual(1, await deleting.ConfigureAwait(false));
            var result = Context(fixture);
            await using var resultLifetime = result.ConfigureAwait(false);
            Assert.IsFalse(await result.Inboxes.AnyAsync(item => item.Id == GatewayEwsFixtureDomain.AccountId).ConfigureAwait(false));
            Assert.IsFalse(await result.Folders.AnyAsync(item => item.InboxId == GatewayEwsFixtureDomain.AccountId).ConfigureAwait(false));
        }
        finally
        {
            barrier.Resume();
            await ObserveMutationCleanupAsync(mutation, deadline).ConfigureAwait(false);
        }
    }

    private static async Task<Guid> SeedLockOrderEmailAsync(CaptureFixture fixture)
    {
        var database = Context(fixture);
        await using var lifetime = database.ConfigureAwait(false);
        var id = Guid.CreateVersion7();
        await database.Emails.AddAsync(new EmailDB { Id = id, FolderId = GatewayEwsFixtureDomain.GrandchildId, Uid = 1 }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        return id;
    }

    private static async Task ObserveMutationCleanupAsync<T>(Task<T> mutation, CancellationTokenSource deadline)
    {
        await deadline.CancelAsync().ConfigureAwait(false);
        // The test started this independent transaction; observe it before disposing its fixture.
#pragma warning disable VSTHRD003
        try { await mutation.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
    }

    private static void SetParentCascadeCommand(NpgsqlCommand command, string parent)
    {
        switch (parent)
        {
            case "inboxes": command.CommandText = "DELETE FROM public.inboxes WHERE id=@id"; break;
            case "addresses": command.CommandText = "DELETE FROM public.addresses WHERE id=(SELECT address_id FROM public.inboxes WHERE id=@id)"; break;
            case "users": command.CommandText = "DELETE FROM public.users WHERE id=(SELECT owner_id FROM public.inboxes WHERE id=@id)"; break;
            default: command.CommandText = "DELETE FROM public.companies WHERE id=(SELECT company_id FROM public.users WHERE id=@owner)"; break;
        }
    }

    private static void AssertLegacyDeadlockResult(MailOperationResult result, string? writerError, FolderLockBarrier barrier)
    {
        Assert.IsTrue(string.Equals(writerError, PostgresErrorCodes.DeadlockDetected, StringComparison.Ordinal)
            || barrier.ProviderErrors.Contains(PostgresErrorCodes.DeadlockDetected, StringComparer.Ordinal));
        if (result.Response.Operation == MailOperationKind.MutateFolders) AssertMutationSucceeded(result);
        else Assert.AreEqual(MailOperationFailureReason.InternalFailure,
            ApplicationValueCodec.Decode(result.Response.Data!)!.Deserialize<MailOperationFailure>(JsonSerializerOptions.Web)!.Reason);
    }

    private static async Task<MailOperationResult> RunLockOrderMutationAsync(CaptureFixture fixture, string key, bool delete,
        CancellationToken cancellationToken)
    {
        using var scope = fixture.DomainScopes.CreateScope();
        var command = LockOrderCommand(key, delete);
        var arguments = (JsonObject)JsonSerializer.SerializeToNode(command, JsonSerializerOptions.Web)!;
        var operation = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages], MailOperationKind.MutateFolders,
            arguments, new Dictionary<string, string>(StringComparer.Ordinal));
        return await scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>().ExecuteAsync(operation,
            new AuthenticatedMailUser(GatewayEwsFixtureDomain.OwnerId, "owner@example.test"), Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MailFolderMutationResult> RunParentLockOrderMutationAsync(CaptureFixture fixture, string key,
        CancellationToken cancellationToken)
    {
        // Parent deletion can invalidate the separate post-commit profile read.
        // Exercise the real typed domain mutation/commit, not that unrelated race.
        using var scope = fixture.DomainScopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var lifetime = transaction.ConfigureAwait(false);
        var context = new JmapInvocationContext(new(GatewayEwsFixtureDomain.OwnerId, "owner@example.test"),
            new HashSet<MailFeature> { MailFeature.Basic, MailFeature.Messages }, new Dictionary<string, string>(StringComparer.Ordinal));
        var result = await scope.ServiceProvider.GetRequiredService<IMailFolderMutationService>()
            .MutateAsync(LockOrderCommand(key, delete: false), context, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static MailFolderMutationCommand LockOrderCommand(string key, bool delete)
    {
        var id = $"M{GatewayEwsFixtureDomain.GrandchildId:N}";
        return new(GatewayEwsFixtureDomain.AccountId,
            Encoding.UTF8.GetString(Convert.FromBase64String(key)), true, [],
            delete ? [] : [new(id, new(MailFolderFields.Name, new("LockOrderWinner", null, null, 0, false), [], null))],
            delete ? [new(id)] : []);
    }

    private static void AssertMutationSucceeded(MailOperationResult result)
    {
        Assert.AreEqual(MailOperationKind.MutateFolders, result.Response.Operation);
        var decoded = ApplicationValueCodec.Decode(result.Response.Data!)!.Deserialize<MailFolderMutationResult>(JsonSerializerOptions.Web)!;
        Assert.AreEqual(MailFolderMutationStatus.Ok, decoded.Status);
        Assert.IsTrue(decoded.Updated.All(item => item.Failure is null) && decoded.Destroyed.All(item => item.Failure is null));
    }

    private static async Task AssertLockOrderResultAsync(CaptureFixture fixture, Guid emailId, bool delete)
    {
        var database = Context(fixture);
        await using var lifetime = database.ConfigureAwait(false);
        if (delete)
        {
            Assert.IsFalse(await database.Folders.AnyAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
            Assert.IsFalse(await database.Emails.AnyAsync(item => item.Id == emailId).ConfigureAwait(false));
        }
        else
        {
            Assert.AreEqual("INBOX/A & B/LockOrderWinner",
                (await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
            Assert.IsTrue((await database.Emails.SingleAsync(item => item.Id == emailId).ConfigureAwait(false)).IsRead);
        }
    }

    private static async Task<string?> FinishSqlWriterAsync(Task<int> saving, NpgsqlTransaction transaction, bool delete, bool legacy)
    {
        try
        {
            // Observe the SQL writer started by the owning test, including its deliberate legacy deadlock.
#pragma warning disable VSTHRD003
            var count = await saving.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            await transaction.CommitAsync().ConfigureAwait(false);
            if (!legacy) Assert.AreEqual(delete ? 0 : 1, count);
            return null;
        }
        catch (PostgresException exception) when (string.Equals(exception.SqlState, PostgresErrorCodes.DeadlockDetected, StringComparison.Ordinal) && legacy)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            return exception.SqlState;
        }
    }

    private static async Task AssertBlockedWriterAsync(CaptureFixture fixture, int writerPid, int ownerPid,
        bool legacy, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(fixture.ApplicationConnection);
        await using var lifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var command = connection.CreateCommand();
            await using var commandLifetime = command.ConfigureAwait(false);
            command.CommandText = "SELECT wait_event FROM pg_stat_activity WHERE pid=@writer AND wait_event_type='Lock' AND @owner=ANY(pg_blocking_pids(pid))";
            command.Parameters.AddWithValue("writer", writerPid);
            command.Parameters.AddWithValue("owner", ownerPid);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string waiting)
            {
                Assert.AreEqual(legacy ? "transactionid" : "advisory", waiting, StringComparer.OrdinalIgnoreCase);
                command.CommandText = "SELECT count(*) FROM pg_locks WHERE pid=@writer AND relation='public.folders'::regclass AND mode='RowShareLock' AND granted";
                var shares = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                Assert.AreEqual(legacy, shares > 0);
                return;
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FolderLockBarrier : DbCommandInterceptor
    {
        private int _armed;
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> Locked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> ProviderErrors { get; } = new();
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Resume() => _resume.TrySetResult();

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                Locked.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
                await _resume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is PostgresException exception) ProviderErrors.Enqueue(exception.SqlState);
            return Task.CompletedTask;
        }
    }
}
