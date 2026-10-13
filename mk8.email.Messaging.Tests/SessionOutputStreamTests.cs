using System.Text;
using mk8.email.MailWire;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers these finite session-output controls by reflection.")]
internal sealed class SessionOutputStreamTests
{
    public TestContext? TestContext { get; set; }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task TokenlessWriterCallsCarrySessionCancellationAndRetainActualBody(bool flush)
    {
        Assert.IsNotNull(TestContext);
        using var cancellation = new CancellationTokenSource();
        var inner = new HeldOutput(flush);
        await using var innerLifetime = inner.ConfigureAwait(false);
        var writer = new SessionResponseWriter(inner, Encoding.ASCII, cancellation.Token);
        Task? operation = null;
        try
        {
            operation = flush ? writer.FlushAsync(CancellationToken.None) : writer.WriteLineAsync(new string('x', 8192));
            var admitted = await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(cancellation.Token, admitted);
            await cancellation.CancelAsync().ConfigureAwait(false);
            Assert.IsTrue(admitted.IsCancellationRequested);
            Assert.IsFalse(operation.IsCompleted, "Cancellation cannot claim settlement of the independently held body.");
            Assert.IsFalse(inner.Disposed);
            inner.Release.TrySetResult();
            // This exact task was admitted above; the assertion independently joins it.
#pragma warning disable VSTHRD003
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            await Assert.ThrowsAsync<OperationCanceledException>(() => writer.DisposeAsync().AsTask()).ConfigureAwait(false);
            Assert.IsFalse(inner.Disposed, "The response facade does not own the session stream.");
        }
        finally
        {
            inner.Release.TrySetResult();
            if (operation is not null)
                await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            // Disposal can legitimately observe the same session cancellation.
            var disposing = writer.DisposeAsync().AsTask();
            await disposing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Assert.IsTrue(disposing.IsCompleted);
        }
    }

    private sealed class HeldOutput(bool holdFlush) : Stream
    {
        internal TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !Disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => holdFlush ? HoldAsync(cancellationToken) : Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(HoldAsync(cancellationToken));
        private async Task HoldAsync(CancellationToken token)
        {
            Entered.TrySetResult(token);
            // The fixture unconditionally releases this owned gate before any disposal.
#pragma warning disable VSTHRD003
            await Release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            token.ThrowIfCancellationRequested();
        }
        protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
