using Microsoft.EntityFrameworkCore;
using Npgsql;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private static async Task ObserveWriterCleanupAsync(Task mutation, Task? writer, CancellationTokenSource deadline,
        Exception? originalFailure, bool concurrency = false, bool legacy = false)
    {
        await deadline.CancelAsync().ConfigureAwait(false);
        var tasks = writer is null ? new[] { mutation } : [mutation, writer];
        var joined = Task.WhenAll(tasks);
        try
        {
            // These are the two already-started transactions owned by the test, not replacement work.
            await joined.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        // Observe every fault, retaining assertion failures alongside unexpected cleanup faults.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            var errors = tasks.Where(task => task.IsFaulted).SelectMany(task => task.Exception!.Flatten().InnerExceptions)
                .Where(error => !ReferenceEquals(error, originalFailure)
                    && error is not OperationCanceledException
                    && !(concurrency && ReferenceEquals(tasks.Last(), writer) && writer?.Exception?.Flatten().InnerExceptions.Contains(error) == true
                        && error is DbUpdateConcurrencyException)
                    && !(legacy && error is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })).ToList();
            if (exception is TimeoutException) errors.Add(exception);
            if (errors.Count == 0) return;
            if (originalFailure is not null) errors.Insert(0, originalFailure);
            throw new AggregateException("Owned interleaving tasks failed during cleanup.", errors);
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
