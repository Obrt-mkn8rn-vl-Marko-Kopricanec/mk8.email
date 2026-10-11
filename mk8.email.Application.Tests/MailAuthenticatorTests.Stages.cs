using System.Runtime.ExceptionServices;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

internal sealed partial class MailAuthenticatorTests
{
    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StageObserverRetainsPrimaryAndOrdinaryAuthentication(bool primary)
    {
        var database = CreateDatabase(domainActive: true, companyActive: true);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var observer = new NativeAuthenticationObserver();
        var authenticator = new MailAuthenticator(database, observer);

        var result = await (primary
            ? authenticator.AuthenticatePrimaryAsync(Username, Password, GetTestCancellation())
            : authenticator.AuthenticateAsync(Username, Password, GetTestCancellation())).ConfigureAwait(false);

        Assert.IsNotNull(result);
        Assert.AreEqual(Username, result.Username, StringComparer.Ordinal);
        AssertStages(observer, MailAuthenticationStage.CandidateQuery,
            MailAuthenticationStage.AccountPasswordVerification);
    }

    [TestMethod]
    public async Task StageObserverTracksApplicationPasswordUsageWithoutExposingCredential()
    {
        var database = CreateDatabase(domainActive: true, companyActive: true);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var user = database.Users.Single();
        var id = Guid.NewGuid();
        var password = $"mk8_{id:N}_controlled-secret";
        var credential = new ApplicationPasswordDB
        {
            Id = id,
            User = user,
            Name = "Finite reserved test credential",
            PasswordHash = PasswordHasher.Hash(password),
        };
        await database.ApplicationPasswords.AddAsync(credential, GetTestCancellation()).ConfigureAwait(false);
        await database.SaveChangesAsync(GetTestCancellation()).ConfigureAwait(false);
        string? output = null;
        var observer = new NativeAuthenticationObserver(value => output = value);
        var authenticator = new MailAuthenticator(database, observer);

        var result = await authenticator.AuthenticateAsync(Username, password, GetTestCancellation()).ConfigureAwait(false);

        Assert.IsNotNull(result);
        Assert.AreEqual(user.Id, result.Id);
        Assert.IsNotNull(credential.LastUsedAt);
        AssertStages(observer, MailAuthenticationStage.CandidateQuery,
            MailAuthenticationStage.AccountPasswordVerification,
            MailAuthenticationStage.ApplicationPasswordQuery,
            MailAuthenticationStage.ApplicationPasswordVerification,
            MailAuthenticationStage.ApplicationPasswordUsageUpdate);
        Assert.IsTrue(observer.WritePoint(AuthenticationPoint.Controlled));
        Assert.IsNotNull(output);
        foreach (var privateValue in new[] { Username, password, user.Id.ToString(), id.ToString(), credential.PasswordHash })
            Assert.IsFalse(output.Contains(privateValue, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(MailAuthenticationStage.CandidateQuery)]
    [DataRow(MailAuthenticationStage.AccountPasswordVerification)]
    public async Task StageObserverHeldEntryRetainsActualAuthenticationUntilRelease(MailAuthenticationStage heldStage)
    {
        var callerCancellation = GetTestCancellation();
        var database = CreateDatabase(domainActive: true, companyActive: true);
        var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdTimedOut = 0;
        var observer = new NativeAuthenticationObserver();
        var sink = CreateHeldStageObserver(observer, heldStage, entered, release,
            () => Interlocked.Exchange(ref holdTimedOut, 1));
        var authenticator = new MailAuthenticator(database, sink);
        // The test owns this thread-pool call to exercise a synchronous observer callback.
        // No production authentication operation is moved onto Task.Run.
        var body = Task.Run(() => authenticator.AuthenticateAsync(Username, Password, CancellationToken.None), CancellationToken.None);
        Exception? failure = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), callerCancellation).ConfigureAwait(false);
            Assert.IsFalse(body.IsCompleted);
            var events = observer.Snapshot(AuthenticationPoint.Controlled).StageEvents;
            Assert.AreEqual(heldStage, events[^1].Stage);
            Assert.AreEqual(MailAuthenticationStagePhase.Entered, events[^1].Phase);
        }
#pragma warning disable CA1031 // Retain the original assertion while independently joining the actual finite held authentication task.
        catch (Exception error) when (!IsFatalStageControl(error))
        {
            failure = error;
        }
#pragma warning restore CA1031
        finally
        {
            release.Set();
            try
            {
                await body.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A distinct cleanup failure must not be hidden by an earlier assertion.
            catch (Exception error) when (!IsFatalStageControl(error))
            {
                failure = failure is null ? error : new AggregateException(failure, error);
            }
#pragma warning restore CA1031
        }
        if (!body.IsCompleted)
            throw new InvalidOperationException("Actual authentication remains active; dependency retirement refused", failure);
        release.Dispose();
        await database.DisposeAsync().ConfigureAwait(false);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.AreEqual(0, Volatile.Read(ref holdTimedOut));
        Assert.IsNotNull(await body.ConfigureAwait(false));
        AssertStages(observer, MailAuthenticationStage.CandidateQuery,
            MailAuthenticationStage.AccountPasswordVerification);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SecondaryObserverFaultCannotReplaceAuthenticationResult(bool accepted)
    {
        var database = CreateDatabase(domainActive: true, companyActive: true);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var observer = new ControlledStageObserver((_, _, _) => throw new InvalidOperationException("Diagnostic sink only"));
        var authenticator = new MailAuthenticator(database, observer);

        var result = await authenticator.AuthenticateAsync(
            Username, accepted ? Password : "wrong-password", GetTestCancellation()).ConfigureAwait(false);

        Assert.AreEqual(accepted, result is not null);
    }

    [TestMethod]
    public async Task SecondaryObserverFaultCannotReplaceQueryFaultDetails()
    {
        var database = CreateDatabase(domainActive: true, companyActive: true);
        await database.DisposeAsync().ConfigureAwait(false);
        var baseline = new MailAuthenticator(database);
        var expected = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            baseline.AuthenticateAsync(Username, Password, GetTestCancellation())).ConfigureAwait(false);
        var observer = new ControlledStageObserver((_, _, _) => throw new InvalidOperationException("Diagnostic sink only"));
        var authenticator = new MailAuthenticator(database, observer);

        var actual = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            authenticator.AuthenticateAsync(Username, Password, GetTestCancellation())).ConfigureAwait(false);

        Assert.AreEqual(expected.ObjectName, actual.ObjectName, StringComparer.Ordinal);
        Assert.AreEqual(expected.Message, actual.Message, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task StageObserverRetainsQueryCancellationAndOriginalToken()
    {
        var database = CreateDatabase(domainActive: true, companyActive: true);
        await using var databaseLifetime = database.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var observer = new NativeAuthenticationObserver();
        var authenticator = new MailAuthenticator(database, observer);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            authenticator.AuthenticateAsync(Username, Password, cancellation.Token)).ConfigureAwait(false);

        Assert.AreEqual(cancellation.Token, actual.CancellationToken);
        var events = observer.Snapshot(AuthenticationPoint.Controlled).StageEvents;
        Assert.HasCount(2, events);
        Assert.AreEqual(MailAuthenticationStage.CandidateQuery, events[0].Stage);
        Assert.AreEqual(MailAuthenticationStage.CandidateQuery, events[1].Stage);
        Assert.AreEqual(MailAuthenticationStagePhase.Entered, events[0].Phase);
        Assert.AreEqual(MailAuthenticationStagePhase.Cancelled, events[1].Phase);
        Assert.AreEqual(events[0].Call, events[1].Call);
    }

    [TestMethod]
    public async Task StageObserverRetainsQueryFaultPhase()
    {
        var database = CreateDatabase(domainActive: true, companyActive: true);
        await database.DisposeAsync().ConfigureAwait(false);
        var observer = new NativeAuthenticationObserver();
        var authenticator = new MailAuthenticator(database, observer);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            authenticator.AuthenticateAsync(Username, Password, GetTestCancellation())).ConfigureAwait(false);

        var events = observer.Snapshot(AuthenticationPoint.Controlled).StageEvents;
        Assert.HasCount(2, events);
        Assert.AreEqual(MailAuthenticationStage.CandidateQuery, events[0].Stage);
        Assert.AreEqual(MailAuthenticationStage.CandidateQuery, events[1].Stage);
        Assert.AreEqual(MailAuthenticationStagePhase.Entered, events[0].Phase);
        Assert.AreEqual(MailAuthenticationStagePhase.Faulted, events[1].Phase);
        Assert.AreEqual(events[0].Call, events[1].Call);
    }

    [TestMethod]
    public void StageInventoryIsBoundedCopiedAndUsesIndependentDropAccounting()
    {
        var observer = new NativeAuthenticationObserver();
        var call = Guid.NewGuid();
        observer.Record(call, MailAuthenticationStage.CandidateQuery, MailAuthenticationStagePhase.Entered);
        var old = observer.Snapshot(AuthenticationPoint.Controlled);
        for (var index = 0; index < NativeAuthenticationObserver.Capacity; index++)
            observer.Record(Guid.NewGuid(), MailAuthenticationStage.CandidateQuery, MailAuthenticationStagePhase.Returned);
        var current = observer.Snapshot(AuthenticationPoint.Controlled);

        Assert.HasCount(1, old.StageEvents);
        Assert.AreEqual(call, old.StageEvents[0].Call);
        Assert.HasCount(NativeAuthenticationObserver.Capacity, current.StageEvents);
        Assert.AreEqual(1L, current.StageDropped);
        Assert.AreEqual(0L, current.Dropped);
        Assert.Throws<NotSupportedException>(() => ((IList<AuthenticationStageEvent>)current.StageEvents).Clear());
    }

    private static void AssertStages(NativeAuthenticationObserver observer, params MailAuthenticationStage[] stages)
    {
        var events = observer.Snapshot(AuthenticationPoint.Controlled).StageEvents;
        Assert.HasCount(stages.Length * 2, events);
        Assert.AreNotEqual(Guid.Empty, events[0].Call);
        for (var index = 0; index < stages.Length; index++)
        {
            Assert.AreEqual(stages[index], events[index * 2].Stage);
            Assert.AreEqual(stages[index], events[(index * 2) + 1].Stage);
            Assert.AreEqual(MailAuthenticationStagePhase.Entered, events[index * 2].Phase);
            Assert.AreEqual(MailAuthenticationStagePhase.Returned, events[(index * 2) + 1].Phase);
            Assert.AreEqual(events[0].Call, events[index * 2].Call);
            Assert.AreEqual(events[0].Call, events[(index * 2) + 1].Call);
        }
    }

    private static bool IsFatalStageControl(Exception error) =>
        error is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private CancellationToken GetTestCancellation()
    {
        Assert.IsNotNull(TestContext);
        return TestContext.CancellationToken;
    }

    private static ControlledStageObserver CreateHeldStageObserver(
        NativeAuthenticationObserver observer, MailAuthenticationStage heldStage,
        TaskCompletionSource entered, ManualResetEventSlim release, Action timedOut) =>
        new((call, stage, phase) =>
        {
            observer.Record(call, stage, phase);
            if (stage != heldStage || phase != MailAuthenticationStagePhase.Entered) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                timedOut();
                throw new InvalidOperationException("Finite diagnostic entry hold timed out");
            }
        });

    private sealed class ControlledStageObserver(Action<Guid, MailAuthenticationStage, MailAuthenticationStagePhase> record)
        : IMailAuthenticationStageObserver
    {
        public void Record(Guid call, MailAuthenticationStage stage, MailAuthenticationStagePhase phase) => record(call, stage, phase);
    }
}
