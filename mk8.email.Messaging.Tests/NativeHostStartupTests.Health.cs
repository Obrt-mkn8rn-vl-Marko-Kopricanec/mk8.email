using System.Globalization;
using System.Runtime.ExceptionServices;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mk8.email.Messaging.Tests;

internal sealed partial class NativeHostStartupTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ProductionHealthObservationOwnsControlledBlockedRequestAndCleanup(bool injectFailure)
    {
        Assert.IsNotNull(TestContext);
        var blob = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(blob)) throw new AssertInconclusiveException("An owned Blob development endpoint is required.");
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false)
            ?? throw new AssertInconclusiveException("An owned PostgreSQL development endpoint is required.");
        DirectoryInfo? directory = null;
        BlobContainerClient? container = null;
        var hosts = new List<NativeHostProcess>();
        var scanners = new List<NativeHostScanner>();
        var healthBodies = new List<Task>();
        var injected = injectFailure ? new AssertFailedException("Controlled health observation assertion.") : null;
        Exception? original = null;
        try
        {
            directory = Directory.CreateTempSubdirectory("mk8-native-health-");
            container = new BlobServiceClient(blob).GetBlobContainerClient("mk8-native-health-" + Guid.NewGuid().ToString("N"));
            await RunHostsAsync(database, blob, container.Name, directory.FullName, unknownRecipient: false,
                hosts, scanners, injected: null, healthControl: (port, token) =>
                    StartOwnedHealthBodyAsync(healthBodies, database.ConnectionString, port, injected, token)).ConfigureAwait(false);
            var context = OpenContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            Assert.AreEqual(0, await context.MailQueueMessages.CountAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(0, await context.Emails.CountAsync(TestContext.CancellationToken).ConfigureAwait(false));
        }
        // Preserve the actual assertion/fault while inherited owners join every child.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        { original = exception; }
        await RetireAsync(hosts, scanners, database, container, directory, original, healthBodies).ConfigureAwait(false);
        if (original is not null && !ReferenceEquals(original, injected)) ExceptionDispatchInfo.Capture(original).Throw();
        if (injected is not null) Assert.AreSame(injected, original);
        Assert.IsTrue(hosts.TrueForAll(host => host.Retired));
    }

    private Task StartOwnedHealthBodyAsync(List<Task> bodies, string connection, int port, Exception? injected, CancellationToken token)
    {
        var body = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = body.Task.Unwrap();
        bodies.Add(owned); // Publish before acquisition, synchronous entry or failure.
        body.SetResult(HoldHealthRequestAsync(connection, port, injected, token));
        return owned.WaitAsync(token); // Caller cancellation cannot abandon actual cleanup.
    }

    private async Task HoldHealthRequestAsync(string connectionString, int port, Exception? injected, CancellationToken token)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var baseline = await NativeHealthObservation.ReadAsync(connectionString, token).ConfigureAwait(false);
        Assert.IsGreaterThan(0L, baseline.Completed);
        var connection = new NpgsqlConnection(connectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync(token).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        await BlockHealthTableAsync(connection, transaction, token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var uri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/health/application"));
        NativeHealthPoint? observed = null;
        var sending = NativeHealthObservation.ObserveRequestAsync(requestToken => client.GetAsync(uri, requestToken),
            diagnosticToken => NativeHealthObservation.ReadAsync(connectionString, diagnosticToken),
            point => { observed = point; EmitHealthPoint(point); }, deadline.Token);
        Exception? original = null;
        try
        {
            await RequireHeldHealthAsync(connectionString, baseline.Inbound, sending, token).ConfigureAwait(false);
            if (injected is not null) ExceptionDispatchInfo.Capture(injected).Throw();
            // This exact request task was started above and is retained by actual body
            // cleanup; the assertion callback does not borrow another owner's work.
#pragma warning disable VSTHRD003
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => sending).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            Assert.IsNotNull(observed);
            Assert.IsGreaterThan(baseline.Inbound, observed.Inbound);
            Assert.AreEqual(baseline.Pending, observed.Pending);
            Assert.AreEqual(baseline.Processing, observed.Processing);
            Assert.AreEqual(baseline.Completed, observed.Completed);
        }
        // Capture the primary observation so rollback/cancellation/join faults cannot mask it.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        { original = exception; }
        finally
        {
            await SettleAndDisposeHealthResponseAsync(sending,
                SettleHeldHealthAsync(sending, transaction, deadline, original), original).ConfigureAwait(false);
        }
        Assert.IsTrue(sending.IsCompleted);
        if (original is not null) ExceptionDispatchInfo.Capture(original).Throw();
    }

    internal static async Task SettleAndDisposeHealthResponseAsync(Task<HttpResponseMessage> sending,
        Task settle, Exception? original)
    {
        Exception? settlementError = null;
        Exception? disposalError = null;
        try { await settle.WaitAsync(CancellationToken.None).ConfigureAwait(false); }
        // Retain settlement faults while independently retiring a returned response.
#pragma warning disable CA1031
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
#pragma warning restore CA1031
        { settlementError = error; }
        finally
        {
            // Actual settlement owns the joins. Its ordinary failure cannot skip
            // disposal when release allowed this request to return successfully.
            if (sending.IsCompletedSuccessfully)
            {
                try { using var response = await sending.WaitAsync(CancellationToken.None).ConfigureAwait(false); }
                // A distinct ordinary disposal fault must not replace settlement.
#pragma warning disable CA1031
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
#pragma warning restore CA1031
                { disposalError = error; }
            }
        }
        if (disposalError is null)
        {
            if (settlementError is not null) ExceptionDispatchInfo.Capture(settlementError).Throw();
            return;
        }
        var errors = new List<Exception>();
        if (original is not null) errors.Add(original);
        IEnumerable<Exception> settlementFaults = settlementError is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions : settlementError is not null ? [settlementError] : [];
        errors.AddRange(settlementFaults.Where(error => !errors.Exists(existing => ReferenceEquals(existing, error))));
        if (!errors.Exists(existing => ReferenceEquals(existing, disposalError))) errors.Add(disposalError);
        throw new AggregateException("Owned health settlement/response disposal failed.", errors);
    }

    private static async Task BlockHealthTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var hold = connection.CreateCommand();
        await using var holdLifetime = hold.ConfigureAwait(false);
        hold.Transaction = transaction;
        // SHARE blocks INSERT/UPDATE but not ordinary SELECT. This is an owned
        // deterministic obstruction, NOT reproduction/cause of historical timeouts.
        hold.CommandText = "LOCK TABLE application_requests IN SHARE MODE";
        await hold.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task SettleHeldHealthAsync(Task sending, NpgsqlTransaction transaction,
        CancellationTokenSource deadline, Exception? original)
    {
        var tasks = new List<Task> { sending };
        var errors = new List<Exception>();
        try { tasks.Add(transaction.RollbackAsync(CancellationToken.None)); }
        // An acquisition-time ordinary cleanup fault cannot omit cancellation/join.
#pragma warning disable CA1031
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
#pragma warning restore CA1031
        { errors.Add(error); }
        try { tasks.Add(deadline.CancelAsync()); }
#pragma warning disable CA1031
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
#pragma warning restore CA1031
        { errors.Add(error); }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        // Actual body retains resources until ALL work settles. Outer waits are bounded,
        // and RetireAsync refuses database/container removal if this body stays active.
#pragma warning disable CA1031
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
#pragma warning restore CA1031
        { _ = error; }
        foreach (var task in tasks)
        {
            if (task.IsFaulted)
            {
                errors.AddRange(task.Exception!.Flatten().InnerExceptions.Where(error => !ReferenceEquals(error, original)
                    && !(ReferenceEquals(task, sending) && error is OperationCanceledException)));
            }
            if (task.IsCanceled && !ReferenceEquals(task, sending)) errors.Add(new TaskCanceledException(task));
        }
        if (errors.Count == 0) return;
        if (original is not null) errors.Insert(0, original);
        throw new AggregateException("Owned controlled health cleanup failed.", errors);
    }

    private static async Task RequireHeldHealthAsync(string connectionString, long inboundBefore,
        Task sending, CancellationToken token)
    {
        var connection = new NpgsqlConnection(connectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync(token).ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        command.CommandText = """
            SELECT count(*) FROM pg_locks
            WHERE database=(SELECT oid FROM pg_database WHERE datname=current_database())
                AND relation='application_requests'::regclass AND mode='RowExclusiveLock' AND NOT granted
            """;
        while (true)
        {
            var waiting = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            var point = await NativeHealthObservation.ReadAsync(connectionString, token).ConfigureAwait(false);
            if (waiting is long count && count > 0 && point.Inbound > inboundBefore) return;
            Assert.IsFalse(sending.IsCompleted, "The owned request ended before the controlled obstruction was observed.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), token).ConfigureAwait(false);
        }
    }
}
