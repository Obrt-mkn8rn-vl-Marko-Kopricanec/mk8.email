using System.Runtime.ExceptionServices;
using System.Text.Json;
using mk8.email.Application.Interfaces;

namespace mk8.email.Application.Tests;

internal sealed partial class TransportSecurityTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task NativeAuthenticationObserverPreservesArgumentsMethodAndResult(bool primary, bool authorized)
    {
        const string username = "observer-user@example.invalid";
        const string password = "observer-password-value";
        var user = authorized ? new AuthenticatedMailUser(Guid.NewGuid(), username) : null;
        var inner = new ControlledMailAuthenticator(() => Task.FromResult(user));
        string? output = null;
        var observer = new NativeAuthenticationObserver(value => output = value);
        var authenticator = new ObservedMailAuthenticator(inner, observer);
        using var cancellation = new CancellationTokenSource();

        var result = await InvokeObservedAuthenticationAsync(
            authenticator, primary, username, password, cancellation.Token).ConfigureAwait(false);

        Assert.AreSame(user, result);
        Assert.IsNotNull(inner.Inputs);
        Assert.AreEqual(primary, inner.Inputs.Primary);
        Assert.AreEqual(username, inner.Inputs.Username, StringComparer.Ordinal);
        Assert.AreEqual(password, inner.Inputs.Password, StringComparer.Ordinal);
        Assert.AreEqual(cancellation.Token, inner.Inputs.Cancellation);
        Assert.IsTrue(observer.WritePoint(AuthenticationPoint.Controlled));
        Assert.IsNotNull(output);
        Assert.IsFalse(output.Contains(username, StringComparison.Ordinal));
        Assert.IsFalse(output.Contains(password, StringComparison.Ordinal));
        if (user is not null) Assert.IsFalse(output.Contains(user.Id.ToString(), StringComparison.Ordinal));
        AssertAuthenticationEvents(observer, primary, AuthenticationPhase.Returned);
        using var json = JsonDocument.Parse(output["MK8_NATIVE_AUTH_DIAGNOSTIC ".Length..]);
        Assert.AreEqual("Controlled", json.RootElement.GetProperty("Point").GetString(), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeAuthenticationObserverPreservesSynchronousFaultIdentity(bool primary)
    {
        var expected = new InvalidOperationException("controlled inner failure");
        var inner = new ControlledMailAuthenticator(() => throw expected);
        var observer = new NativeAuthenticationObserver();
        var authenticator = new ObservedMailAuthenticator(inner, observer);
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeObservedAuthenticationAsync(authenticator, primary, "user", "password", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreSame(expected, actual);
        AssertAuthenticationEvents(observer, primary, AuthenticationPhase.Faulted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeAuthenticationObserverPreservesCancellationToken(bool primary)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var inner = new ControlledMailAuthenticator(() => Task.FromCanceled<AuthenticatedMailUser?>(cancellation.Token));
        var observer = new NativeAuthenticationObserver();
        var authenticator = new ObservedMailAuthenticator(inner, observer);
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            InvokeObservedAuthenticationAsync(authenticator, primary, "user", "password", cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(cancellation.Token, actual.CancellationToken);
        AssertAuthenticationEvents(observer, primary, AuthenticationPhase.Cancelled);
    }

    [TestMethod]
    public async Task NativeAuthenticationObserverCorrelatesActuallyOverlappingCalls()
    {
        var first = new TaskCompletionSource<AuthenticatedMailUser?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AuthenticatedMailUser?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var inner = new ControlledMailAuthenticator(() => ++count == 1 ? first.Task : second.Task);
        var observer = new NativeAuthenticationObserver();
        var authenticator = new ObservedMailAuthenticator(inner, observer);
        var firstCall = authenticator.AuthenticateAsync("first", "password", CancellationToken.None);
        var secondCall = authenticator.AuthenticatePrimaryAsync("second", "password", CancellationToken.None);
        Exception? failure = null;
        try
        {
            Assert.IsFalse(firstCall.IsCompleted);
            Assert.IsFalse(secondCall.IsCompleted);
            var events = observer.Snapshot(AuthenticationPoint.Controlled).Events;
            Assert.HasCount(2, events);
            Assert.IsTrue(events.All(item => item.Phase == AuthenticationPhase.Entered));
            Assert.AreNotEqual(events[0].Span, events[1].Span);
        }
        catch (Exception error) when (!IsFatalAuthenticationControl(error))
        {
            failure = error;
        }
        finally
        {
            first.TrySetResult(null);
            second.TrySetResult(null);
            failure = await ObserveOwnedAuthenticationCleanupAsync(Task.WhenAll(firstCall, secondCall), failure).ConfigureAwait(false);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        var completed = observer.Snapshot(AuthenticationPoint.Controlled).Events;
        Assert.HasCount(4, completed);
        foreach (var group in completed.GroupBy(item => item.Span))
        {
            Assert.AreSequenceEqual<AuthenticationPhase>(
                [AuthenticationPhase.Entered, AuthenticationPhase.Returned],
                [.. group.Select(item => item.Phase)]);
            Assert.AreEqual(group.First().Primary, group.Last().Primary);
        }
    }

    [TestMethod]
    public void NativeAuthenticationObserverBoundsAndCopiesItsInventory()
    {
        var observer = new NativeAuthenticationObserver();
        var oldSpan = observer.Enter(primary: false);
        var oldSnapshot = observer.Snapshot(AuthenticationPoint.Controlled);
        for (var index = 0; index < NativeAuthenticationObserver.Capacity; index++)
            observer.Enter(primary: true);
        var current = observer.Snapshot(AuthenticationPoint.Controlled);
        Assert.HasCount(1, oldSnapshot.Events);
        Assert.AreEqual(oldSpan, oldSnapshot.Events[0].Span);
        Assert.HasCount(NativeAuthenticationObserver.Capacity, current.Events);
        Assert.AreEqual(1L, current.Dropped);
        Assert.DoesNotContain(item => item.Span == oldSpan, current.Events);
        Assert.Throws<NotSupportedException>(() => ((IList<AuthenticationEvent>)current.Events).Clear());
        Assert.HasCount(NativeAuthenticationObserver.Capacity, observer.Snapshot(AuthenticationPoint.Controlled).Events);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeAuthenticationObserverContainsDeclaredOutputFailures(bool ioFailure)
    {
        var observer = new NativeAuthenticationObserver(_ =>
        {
            if (ioFailure) throw new IOException("controlled output failure");
            throw new InvalidOperationException("controlled output failure");
        });
        Assert.IsFalse(observer.WritePoint(AuthenticationPoint.Controlled));
    }

    private static Task<AuthenticatedMailUser?> InvokeObservedAuthenticationAsync(
        IMailAuthenticator authenticator, bool primary, string username, string password, CancellationToken cancellationToken) =>
        primary ? authenticator.AuthenticatePrimaryAsync(username, password, cancellationToken)
                : authenticator.AuthenticateAsync(username, password, cancellationToken);

    private static void AssertAuthenticationEvents(
        NativeAuthenticationObserver observer, bool primary, AuthenticationPhase terminal)
    {
        var events = observer.Snapshot(AuthenticationPoint.Controlled).Events;
        Assert.HasCount(2, events);
        Assert.AreEqual(AuthenticationPhase.Entered, events[0].Phase);
        Assert.AreEqual(terminal, events[1].Phase);
        Assert.AreEqual(events[0].Span, events[1].Span);
        Assert.AreNotEqual(Guid.Empty, events[0].Span);
        Assert.AreEqual(primary, events[0].Primary);
        Assert.AreEqual(primary, events[1].Primary);
        Assert.IsGreaterThanOrEqualTo(events[0].ElapsedMilliseconds, events[1].ElapsedMilliseconds);
    }

    private sealed record ControlledAuthenticationInputs(
        bool Primary, string Username, string Password, CancellationToken Cancellation);

    private sealed class ControlledMailAuthenticator(Func<Task<AuthenticatedMailUser?>> call) : IMailAuthenticator
    {
        public ControlledAuthenticationInputs? Inputs { get; private set; }
        public Task<AuthenticatedMailUser?> AuthenticateAsync(
            string username, string password, CancellationToken cancellationToken = default) =>
            InvokeAsync(primary: false, username, password, cancellationToken);
        public Task<AuthenticatedMailUser?> AuthenticatePrimaryAsync(
            string username, string password, CancellationToken cancellationToken = default) =>
            InvokeAsync(primary: true, username, password, cancellationToken);
        private Task<AuthenticatedMailUser?> InvokeAsync(bool primary, string username, string password, CancellationToken token)
        {
            Inputs = new ControlledAuthenticationInputs(primary, username, password, token);
            return call();
        }
    }
}
