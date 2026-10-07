using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Infrastructure.Data;
using Npgsql;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private static async Task ObserveWriterCleanupAsync(Task mutation, Task? writer, CancellationTokenSource deadline,
        Exception? originalFailure, bool concurrency = false, bool legacy = false, Func<CancellationToken, Task>? rollback = null)
    {
        var tasks = new List<Task> { mutation };
        if (writer is not null) tasks.Add(writer);
        var errors = new List<Exception>();
        using var rollbackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (rollback is not null)
            await RunCleanupStageAsync(() => rollback(rollbackDeadline.Token), tasks, errors).ConfigureAwait(false);
        // A failed rollback or cancellation callback cannot skip observation of owned work.
        await RunCleanupStageAsync(deadline.CancelAsync, tasks, errors).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        // The tasks' complete fault inventories are inspected below, not just WhenAll's first fault.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            if (exception is TimeoutException) errors.Add(exception);
        }
        errors.AddRange(tasks.Where(task => task.IsFaulted).SelectMany(task => task.Exception!.Flatten().InnerExceptions)
            .Where(error => !ReferenceEquals(error, originalFailure) && !errors.Contains(error)
                && error is not OperationCanceledException
                && !(concurrency && writer?.Exception?.Flatten().InnerExceptions.Contains(error) == true && error is DbUpdateConcurrencyException)
                && !(legacy && error is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })));
        if (errors.Count == 0) return;
        if (originalFailure is not null) errors.Insert(0, originalFailure);
        throw new AggregateException("Owned interleaving tasks failed during cleanup.", errors);
    }

    private static async Task RunCleanupStageAsync(Func<Task> start, List<Task> tasks, List<Exception> errors)
    {
        try
        {
            var task = start();
            tasks.Add(task); // Retain even a timed-out stage until the final bounded join.
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        // Record cleanup faults, then continue cancellation and observation before dependency disposal.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            if (exception is AggregateException aggregate) errors.AddRange(aggregate.Flatten().InnerExceptions);
            else errors.Add(exception);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CleanupRetainsOriginalAssertionAndUnexpectedTaskFault(bool writerFault)
    {
        using var deadline = new CancellationTokenSource();
        var original = new AssertFailedException("Original observation assertion.");
        var fatal = new InvalidOperationException("Unexpected writer/transaction failure.");
        var mutation = writerFault ? Task.CompletedTask : Task.FromException(fatal);
        var writer = writerFault ? Task.FromException(fatal) : Task.CompletedTask;
        // The deliberate already-faulted inputs are the faults under test, not replacement work.
#pragma warning disable VSTHRD003
        var error = await Assert.ThrowsAsync<AggregateException>(() => ObserveWriterCleanupAsync(mutation, writer, deadline, original)).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        CollectionAssert.AreEqual(new Exception[] { original, fatal }, error.InnerExceptions.ToArray());
    }

    [TestMethod]
    public async Task RollbackFailureCancelsAndObservesBlockedSaveWithoutReplacingOriginalFailure()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var emailId = await SeedLockOrderEmailAsync(fixture).ConfigureAwait(false);
        var rollbackFailure = new InvalidOperationException("Controlled rollback failure.");
        var interceptor = new FailingRollbackInterceptor(rollbackFailure);
        var owner = new EmailDbContext(new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql(fixture.ApplicationConnection).AddInterceptors(interceptor).Options);
        await using var ownerLifetime = owner.ConfigureAwait(false);
        var transaction = await owner.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var writer = Context(fixture);
        await using var writerLifetime = writer.ConfigureAwait(false);
        await writer.Database.OpenConnectionAsync().ConfigureAwait(false);
        (await writer.Emails.SingleAsync(item => item.Id == emailId).ConfigureAwait(false)).IsRead = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var saving = writer.SaveChangesAsync(deadline.Token);
        AssertFailedException? original = null;
        AggregateException? cleanupFailure = null;
        try
        {
            await AssertBlockedWriterAsync(fixture, ((NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID,
                ((NpgsqlConnection)owner.Database.GetDbConnection()).ProcessID, legacy: false, deadline.Token).ConfigureAwait(false);
            Assert.Fail("Controlled assertion before rollback.");
        }
        catch (AssertFailedException exception) { original = exception; }
        finally
        {
            try
            {
                await ObserveWriterCleanupAsync(Task.CompletedTask, saving, deadline, original,
                    rollback: transaction.RollbackAsync).ConfigureAwait(false);
            }
            catch (AggregateException exception) { cleanupFailure = exception; }
        }
        Assert.IsNotNull(original);
        Assert.IsNotNull(cleanupFailure);
        CollectionAssert.AreEqual(new Exception[] { original, rollbackFailure }, cleanupFailure.InnerExceptions.ToArray());
        Assert.IsTrue(interceptor.Attempted);
        Assert.IsTrue(deadline.IsCancellationRequested);
        Assert.IsTrue(saving.IsCompleted);
        Assert.IsNull(writer.Database.CurrentTransaction);
        Assert.AreSame(transaction, owner.Database.CurrentTransaction);
        // Both contexts and the failed-rollback transaction are still alive until these assertions complete.
    }

    private sealed class FailingRollbackInterceptor(Exception failure) : DbTransactionInterceptor
    {
        public bool Attempted { get; private set; }

        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(System.Data.Common.DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempted = true;
            throw failure;
        }
    }

    [TestMethod]
    [DataRow("sql")]
    [DataRow("ef")]
    [DataRow("parent")]
    public async Task AssertionFailureWhileWriterIsBlockedJoinsBothTasksBeforeDisposal(string kind)
    {
        var barrier = new FolderLockBarrier();
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, folderLockInterceptor: barrier).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var emailId = await SeedLockOrderEmailAsync(fixture).ConfigureAwait(false);
        Authenticate(fixture);
        var key = await ReadKeyAsync(fixture, FolderId(GatewayEwsFixtureDomain.GrandchildId)).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.OpenConnectionAsync().ConfigureAwait(false);
        (await database.Emails.SingleAsync(item => item.Id == emailId).ConfigureAwait(false)).IsRead = true;
        var connection = new NpgsqlConnection(fixture.ApplicationConnection);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        if (kind is "parent") command.CommandText = "DELETE FROM public.inboxes WHERE id=@id";
        else command.CommandText = "UPDATE public.emails SET is_read=true WHERE id=@id";
        command.Parameters.AddWithValue("id", kind is "parent" ? GatewayEwsFixtureDomain.AccountId : emailId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        barrier.Arm();
        var mutation = RunLockOrderMutationAsync(fixture, key, delete: false, deadline.Token);
        Task<int>? writer = null;
        AssertFailedException? assertion = null;
        try
        {
            var ownerPid = await barrier.Locked.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            writer = kind is "ef" ? database.SaveChangesAsync(deadline.Token) : command.ExecuteNonQueryAsync(deadline.Token);
            var writerPid = kind is "ef" ? ((NpgsqlConnection)database.Database.GetDbConnection()).ProcessID : connection.ProcessID;
            await AssertBlockedWriterAsync(fixture, writerPid, ownerPid, legacy: false, deadline.Token).ConfigureAwait(false);
            Assert.Fail("Controlled observation-stage assertion failure.");
        }
        catch (AssertFailedException exception) { assertion = exception; }
        finally
        {
            barrier.Resume();
            await ObserveWriterCleanupAsync(mutation, writer, deadline, assertion).ConfigureAwait(false);
        }
        Assert.IsNotNull(assertion);
        Assert.IsTrue(assertion.Message.Contains("Controlled observation-stage assertion failure.", StringComparison.Ordinal));
        Assert.IsTrue(mutation.IsCompleted);
        Assert.IsNotNull(writer);
        Assert.IsTrue(writer.IsCompleted);
        // Connection/context are still alive here; disposal only follows completed/observed tasks.
        Assert.IsNull(database.Database.CurrentTransaction);
    }
}
