using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using mk8.email.Configuration;
using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Smtp.Presentation;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest reflects these finite actual presentation-worker/body ownership controls.")]
internal sealed class PresentationSettlementTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public TestContext? TestContext { get; set; }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task HeldBodiesKeepSlotsUntilActualSettlementThenPermitSmtpProgress()
    {
        Assert.IsNotNull(TestContext);
        var clock = new AttemptClock();
        var held = new HeldBodies();
        using var handler = new HeldHeadersHandler(held);
        using var sender = new GatewayWebPushService(handler, new Journal(), 65_536, clock);
        var requests = new Requests();
        var relay = new Relay();
        var log = new WorkerLog();
        using var worker = CreateWorker(requests, sender, relay, log);
        for (var index = 0; index < 16; index++)
        {
            // Finite reserved hosts routed ONLY through the injected in-memory handler.
            var push = new WebPushSendRequest(string.Create(CultureInfo.InvariantCulture, $"https://push-{index}.example.invalid/delivery"), P256dh: null, Auth: null,
                DateTimeOffset.UtcNow.AddDays(2), "{\"finite\":true}"u8.ToArray());
            await requests.EnqueueAsync(Request(WebPushPresentationOperations.Protocol, WebPushPresentationOperations.Send,
                push), TestContext.CancellationToken).ConfigureAwait(false);
        }
        await requests.EnqueueAsync(SmtpRequest(), TestContext.CancellationToken).ConfigureAwait(false);
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await held.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(16, requests.Claims);
            Assert.IsFalse(relay.Completed.Task.IsCompleted);
            Assert.HasCount(16, clock.Timers);
            Assert.IsTrue(clock.Timers.All(timer => timer.DueTime == TimeSpan.FromSeconds(10)));
            clock.ExpireAllAttempts();
            await held.AllCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(16, requests.Claims, "Cancellation alone must not release still-held presentation slots.");
            Assert.IsFalse(relay.Completed.Task.IsCompleted);
            Assert.IsTrue(held.Streams.All(stream => !stream.Settled && !stream.Disposed));
            held.Release.TrySetResult();
            await relay.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.CancellationToken).ConfigureAwait(false);
            await requests.SmtpCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.CancellationToken).ConfigureAwait(false);
            await requests.AllPushFailed.Task.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(17, requests.Claims);
            Assert.AreEqual(16, requests.Failures);
            Assert.IsTrue(held.Streams.All(stream => stream.Settled && stream.Disposed));
        }
        finally
        {
            held.Release.TrySetResult();
            // This is the actual shared stop, not a canceled caller wait.
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow("success", false)]
    [DataRow("lost", false)]
    [DataRow("fault", false)]
    [DataRow("fault", true)]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RenewalOutcomeCancelsCooperativeEffectAndRetainsHeldSettlement(string renewal, bool callbackFault)
    {
        Assert.IsNotNull(TestContext);
        var renewalError = new IOException("Distinguished lease-renewal failure.");
        var callbackError = new InvalidOperationException("Distinguished operation cancellation callback failure.");
        var requests = new Requests { Renewal = renewal, RenewalError = renewalError };
        var relay = new Relay { Hold = true, CallbackError = callbackFault ? callbackError : null };
        var log = new WorkerLog();
        using var handler = new RefusingHandler();
        using var sender = new GatewayWebPushService(handler, new Journal(), 65_536);
        using var worker = CreateWorker(requests, sender, relay, log);
        await requests.EnqueueAsync(SmtpRequest(), TestContext.CancellationToken).ConfigureAwait(false);
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await relay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false);
            await requests.Renewed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false);
            if (!string.Equals(renewal, "success", StringComparison.Ordinal))
            {
                await relay.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsFalse(relay.Settled);
                Assert.IsFalse(requests.Terminal.Task.IsCompleted);
                Assert.AreEqual(0, relay.Effects);
            }
            relay.Release.TrySetResult();
            await requests.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            relay.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        Assert.IsTrue(relay.Settled);
        Assert.AreEqual(string.Equals(renewal, "success", StringComparison.Ordinal) ? 1 : 0, relay.Effects);
        if (string.Equals(renewal, "fault", StringComparison.Ordinal))
        {
            Assert.Contains(error => ReferenceEquals(error, renewalError), log.Failures.SelectMany(Leaves));
            if (callbackFault)
            {
                Assert.Contains(error => ReferenceEquals(error, callbackError), log.Failures.SelectMany(Leaves));
            }
        }
    }

    private static IEnumerable<Exception> Leaves(Exception error) => error is AggregateException aggregate
        ? aggregate.Flatten().InnerExceptions : [error];

    private static GatewayPresentationWorker CreateWorker(Requests requests, GatewayWebPushService sender,
        Relay relay, WorkerLog logger) => new(requests, new Availability(), new Journal(), sender, relay,
            new EnvironmentConfig { Messaging = new MessagingConfig { LeaseSeconds = 5 } }, logger);

    private static ApplicationRequest SmtpRequest() => Request(SmtpPresentationOperations.Protocol,
        SmtpPresentationOperations.Relay, new SmtpRelayPresentationRequest("sender@example.test",
            "recipient@example.test", "Subject: finite\r\n\r\nbody\r\n", Options: null));

    private static ApplicationRequest Request<T>(string protocol, string operation, T value)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, protocol, operation, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            new Dictionary<string, string>(StringComparer.Ordinal), now, now.AddDays(1));
    }

    private sealed class Requests : IPresentationRequestConsumer
    {
        private readonly Channel<ApplicationRequestLease> queue = Channel.CreateUnbounded<ApplicationRequestLease>();
        private int claims;
        private int failures;
        internal int Claims => Volatile.Read(ref claims);
        internal int Failures => Volatile.Read(ref failures);
        internal string Renewal { get; init; } = "success";
        internal Exception? RenewalError { get; init; }
        internal TaskCompletionSource Renewed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SmtpCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllPushFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ValueTask EnqueueAsync(ApplicationRequest request, CancellationToken token) => queue.Writer.WriteAsync(
            new(request, "finite-worker", DateTimeOffset.UtcNow.AddSeconds(5), 1), token);
        public async Task<ApplicationRequestLease> WaitForRequestAsync(string workerId, CancellationToken cancellationToken = default)
        {
            var value = await queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref claims);
            return value;
        }
        public Task<ApplicationRequestLease?> TryClaimAsync(string workerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> RenewLeaseAsync(ApplicationRequestLease lease, CancellationToken cancellationToken = default)
        {
            Renewed.TrySetResult();
            if (string.Equals(Renewal, "fault", StringComparison.Ordinal))
                return Task.FromException<bool>(RenewalError ?? throw new InvalidOperationException("Missing controlled renewal fault."));
            return Task.FromResult(!string.Equals(Renewal, "lost", StringComparison.Ordinal));
        }
        public Task CompleteAsync(ApplicationRequestLease lease, ApplicationResponse response, CancellationToken cancellationToken = default)
        {
            Assert.IsFalse(response.IsError);
            if (string.Equals(lease.Request.Protocol, SmtpPresentationOperations.Protocol, StringComparison.Ordinal))
                SmtpCompleted.TrySetResult();
            Terminal.TrySetResult();
            return Task.CompletedTask;
        }
        public Task FailAsync(ApplicationRequestLease lease, string errorCode, string errorDetail, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref failures) == 16) AllPushFailed.TrySetResult();
            Terminal.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class Relay : ISmtpPresentationRelay
    {
        internal bool Hold { get; init; }
        internal Exception? CallbackError { get; init; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Effects { get; private set; }
        internal bool Settled { get; private set; }
        public async Task<OutboundDeliveryResult> RelayAsync(SmtpRelayPresentationRequest request,
            Guid applicationRequestId, CancellationToken cancellationToken)
        {
            var registration = cancellationToken.Register(() =>
            {
                Cancelled.TrySetResult();
                if (CallbackError is { } error) throw error;
            });
            await using var registrationLifetime = registration.ConfigureAwait(false);
            Entered.TrySetResult();
            try
            {
                if (Hold)
                {
                    // Always released in the outer finally; cancellation is observed before the effect gate.
#pragma warning disable VSTHRD003
                    await Release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                }
                cancellationToken.ThrowIfCancellationRequested();
                Effects++;
                Completed.TrySetResult();
                return new(OutboundDeliveryStatus.Delivered, "Finite in-memory relay effect.");
            }
            finally { Settled = true; }
        }
    }

    private sealed class Availability : IApplicationTransportControl
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class Journal : IGatewayTrafficJournal
    {
        public Task AppendAsync(GatewayTrafficRecord record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class WorkerLog : ILogger<GatewayPresentationWorker>
    {
        internal ConcurrentQueue<Exception> Failures { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Failures.Enqueue(exception);
        }
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new AssertFailedException("The SMTP-only control must not dispatch HTTP.");
    }

    private sealed class HeldHeadersHandler(HeldBodies bodies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var stream = new HeldBodyStream(bodies);
            bodies.Streams.Enqueue(stream);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }
    }

    private sealed class HeldBodies
    {
        internal int Entered;
        internal int Cancelled;
        internal ConcurrentQueue<HeldBodyStream> Streams { get; } = new();
        internal TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class HeldBodyStream(HeldBodies bodies) : Stream
    {
        internal bool Settled { get; private set; }
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref bodies.Entered) == 16) bodies.AllEntered.TrySetResult();
            try
            {
                // The read cooperatively observes cancellation, then holds its
                // independent finite cleanup so cancellation cannot masquerade as settlement.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (Interlocked.Increment(ref bodies.Cancelled) == 16) bodies.AllCancelled.TrySetResult();
                // The outer finally releases every cleanup before joining the worker.
#pragma warning disable VSTHRD003
                await bodies.Release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                throw;
            }
            finally { Settled = true; }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Assert.IsTrue(Settled, "Response content cannot be disposed before its actual read settles.");
                Disposed = true;
            }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AttemptClock : TimeProvider
    {
        internal ConcurrentQueue<AttemptTimer> Timers { get; } = new();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new AttemptTimer(callback, state, dueTime, period);
            Timers.Enqueue(timer);
            return timer;
        }
        internal void ExpireAllAttempts()
        {
            foreach (var timer in Timers) timer.Fire();
        }
    }

    private sealed class AttemptTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private int disposed;
        private int fired;
        internal TimeSpan DueTime { get; private set; } = dueTime;
        internal TimeSpan Period { get; private set; } = period;
        public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
        {
            if (Volatile.Read(ref disposed) != 0) return false;
            DueTime = newDueTime;
            Period = newPeriod;
            return true;
        }
        internal void Fire()
        {
            Assert.AreEqual(Timeout.InfiniteTimeSpan, Period);
            if (Volatile.Read(ref disposed) == 0 && Interlocked.Exchange(ref fired, 1) == 0) callback(state);
        }
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
