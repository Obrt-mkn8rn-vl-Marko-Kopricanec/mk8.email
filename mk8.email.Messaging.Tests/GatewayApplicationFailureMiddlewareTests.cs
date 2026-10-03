using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols;
using mk8.email.Messaging;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class GatewayApplicationFailureMiddlewareTests
{
    [TestMethod]
    public async Task OfflineApplicationReturnsStableOAuthAvailabilityError()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/oauth/token";
        context.Response.Body = new MemoryStream();
        var middleware = new GatewayApplicationFailureMiddleware(
            _ => throw new GatewayApplicationException(
                "application-timeout",
                "simulated unavailable worker",
                isUnavailable: true),
            NullLogger<GatewayApplicationFailureMiddleware>.Instance);

        await middleware.InvokeAsync(context).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.AreEqual("5", context.Response.Headers.RetryAfter.ToString(), StringComparer.Ordinal);
        context.Response.Body.Position = 0;
        using var ownedResource1 = new StreamReader(
                    context.Response.Body,
                    Encoding.UTF8);
        var body = await ownedResource1.ReadToEndAsync().ConfigureAwait(false);
        StringAssert.Contains(body, "temporarily_unavailable", StringComparison.Ordinal);
        Assert.IsFalse(body.Contains("simulated unavailable worker", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OAuthPresentationTrafficIsRecordedWithoutAnApplicationCall()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/oauth/token";
        context.Request.QueryString = new QueryString("?trace=external-request");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(
            Encoding.UTF8.GetBytes("grant_type=refresh_token&refresh_token=secret-value"));
        context.Response.Body = new MemoryStream();
        var journal = new StubTrafficJournal();
        var middleware = new GatewayProtocolTrafficCaptureMiddleware(
            async requestContext =>
            {
                requestContext.Response.StatusCode = StatusCodes.Status200OK;
                requestContext.Response.ContentType = "application/json";
                await requestContext.Response.WriteAsync("{\"ok\":true}").ConfigureAwait(false);
            },
            NullLogger<GatewayProtocolTrafficCaptureMiddleware>.Instance);

        await middleware.InvokeAsync(
            context,
            journal,
            new GatewayApplicationOptions(
                "gateway@test-host",
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10)),
            new EnvironmentConfig()).ConfigureAwait(false);

        Assert.HasCount(2, journal.Records);
        Assert.AreEqual(GatewayTrafficDirections.Inbound, journal.Records[0].Direction, StringComparer.Ordinal);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, journal.Records[1].Direction, StringComparer.Ordinal);
        Assert.AreEqual(journal.Records[0].SessionId, journal.Records[1].SessionId);
        using var inbound = JsonDocument.Parse(journal.Records[0].Payload);
        Assert.AreEqual("/oauth/token", inbound.RootElement.GetProperty("path").GetString(), StringComparer.Ordinal);
        Assert.AreEqual(
            "grant_type=refresh_token&refresh_token=secret-value",
            Encoding.UTF8.GetString(Convert.FromBase64String(
                inbound.RootElement.GetProperty("bodyBase64").GetString()!)), StringComparer.Ordinal);
        context.Response.Body.Position = 0;
        using var ownedResource2 = new StreamReader(context.Response.Body, Encoding.UTF8);
        Assert.AreEqual(
            "{\"ok\":true}",
            await ownedResource2.ReadToEndAsync().ConfigureAwait(false), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task JmapEventStreamRecordsResponseStartAndEveryFlushedChunk()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/jmap/event";
        context.Request.QueryString = new QueryString("?types=*&closeafter=no&ping=15");
        context.Response.Body = new MemoryStream();
        var journal = new StubTrafficJournal();
        var middleware = new GatewayProtocolTrafficCaptureMiddleware(
            async requestContext =>
            {
                requestContext.Response.StatusCode = StatusCodes.Status200OK;
                requestContext.Response.ContentType = "text/event-stream";
                await requestContext.Response.StartAsync().ConfigureAwait(false);
                await requestContext.Response.WriteAsync("event: ping\ndata: {\"interval\":15}\n\n").ConfigureAwait(false);
                await requestContext.Response.WriteAsync("event: ping\ndata: {\"interval\":15}\n\n").ConfigureAwait(false);
            },
            NullLogger<GatewayProtocolTrafficCaptureMiddleware>.Instance);

        await middleware.InvokeAsync(
            context,
            journal,
            new GatewayApplicationOptions(
                "gateway@test-host",
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10)),
            new EnvironmentConfig
            {
                Jmap = new JmapConfig
                {
                    MaxRequestSizeBytes = 65_536,
                    MaxUploadSizeBytes = 1_048_576,
                },
            }).ConfigureAwait(false);

        Assert.HasCount(4, journal.Records);
        Assert.AreEqual(GatewayTrafficDirections.Inbound, journal.Records[0].Direction, StringComparer.Ordinal);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, journal.Records[1].Direction, StringComparer.Ordinal);
        Assert.AreEqual("application/vnd.mk8.gateway-http+json", journal.Records[1].ContentType, StringComparer.Ordinal);
        Assert.AreEqual(
            "application/vnd.mk8.gateway-http-chunk+json",
            journal.Records[2].ContentType, StringComparer.Ordinal);
        Assert.AreEqual(
            "application/vnd.mk8.gateway-http-chunk+json",
            journal.Records[3].ContentType, StringComparer.Ordinal);
        Assert.IsTrue(journal.Records.All(record => string.Equals(record.Protocol, "jmap", StringComparison.Ordinal)));
        Assert.IsTrue(journal.Records.All(record => record.SessionId == journal.Records[0].SessionId));
    }

    [TestMethod]
    public async Task UnknownLengthBodyStopsAtLimitPlusOneAndRecordsRejectionWithoutDispatch()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/oauth/token";
        var input = new LimitProbeStream();
        await using var inputLifetime = input.ConfigureAwait(false);
        context.Request.Body = input;
        var output = new MemoryStream();
        await using var outputLifetime = output.ConfigureAwait(false);
        context.Response.Body = output;
        var calls = 0;
        var journal = new StubTrafficJournal();
        var middleware = new GatewayProtocolTrafficCaptureMiddleware(_ =>
        {
            calls++;
            return Task.CompletedTask;
        }, NullLogger<GatewayProtocolTrafficCaptureMiddleware>.Instance);
        await middleware.InvokeAsync(context, journal,
            new GatewayApplicationOptions("gateway@test", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)),
            new EnvironmentConfig()).ConfigureAwait(false);

        Assert.AreEqual(65_537, input.BytesRead);
        Assert.AreEqual(0, calls);
        Assert.AreEqual(413, context.Response.StatusCode);
        Assert.HasCount(2, journal.Records);
        using var inbound = JsonDocument.Parse(journal.Records[0].Payload);
        Assert.AreEqual(65_537, inbound.RootElement.GetProperty("rejection").GetProperty("observedBytes").GetInt32());
        Assert.AreEqual(string.Empty, inbound.RootElement.GetProperty("bodyBase64").GetString(), StringComparer.Ordinal);
        Assert.AreSame(input, context.Request.Body);
    }

    [TestMethod]
    public void CaptureBufferBoundsEveryWriteAndSetLength()
    {
        using var buffer = new GatewayCaptureBuffer(65_536, 413);
        buffer.Write(new byte[65_536]);
        Assert.IsLessThanOrEqualTo(65_536, buffer.Capacity);
        Assert.ThrowsExactly<BadHttpRequestException>(() => buffer.WriteByte(1));
        Assert.ThrowsExactly<BadHttpRequestException>(() => buffer.SetLength(65_537));
        Assert.AreEqual(65_536L, buffer.Length);
    }

    [TestMethod]
    [DataRow(GatewayTrafficDirections.Inbound, 0)]
    [DataRow(GatewayTrafficDirections.Outbound, 1)]
    public async Task JournalFailureRemainsFailClosed(string failedDirection, int expectedCalls)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/oauth/token";
        var output = new MemoryStream();
        await using var outputLifetime = output.ConfigureAwait(false);
        context.Response.Body = output;
        var journal = new StubTrafficJournal { FailedDirection = failedDirection };
        var calls = 0;
        var middleware = new GatewayProtocolTrafficCaptureMiddleware(async request =>
        {
            calls++;
            await request.Response.WriteAsync("business-secret").ConfigureAwait(false);
        }, NullLogger<GatewayProtocolTrafficCaptureMiddleware>.Instance);
        await middleware.InvokeAsync(context, journal,
            new GatewayApplicationOptions("gateway@test", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)),
            new EnvironmentConfig()).ConfigureAwait(false);
        Assert.AreEqual(expectedCalls, calls);
        Assert.AreEqual(503, context.Response.StatusCode);
        var body = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(body, "temporarily_unavailable", StringComparison.Ordinal);
        Assert.IsFalse(body.Contains("business-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedOutboundBodyOrMetadataBecomesJournaledFailureNotUnavailable(bool oversizedHeaders)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/jmap/api";
        var output = new MemoryStream();
        await using var outputLifetime = output.ConfigureAwait(false);
        context.Response.Body = output;
        var journal = new StubTrafficJournal();
        var environment = new EnvironmentConfig
        {
            Messaging = new MessagingConfig { MaxPayloadBytes = 2_500_000 },
        };
        var middleware = new GatewayProtocolTrafficCaptureMiddleware(async request =>
        {
            await request.Response.WriteAsync("partial-business-secret").ConfigureAwait(false);
            if (oversizedHeaders)
                request.Response.Headers["X-Too-Large"] = new string('x', 32_769);
            else
                await request.Response.Body.WriteAsync(new byte[checked((int)GatewayHttpPayloadBudget
                    .MaximumBinaryBodyBytes(environment.Messaging.MaxPayloadBytes) + 1)]).ConfigureAwait(false);
        }, NullLogger<GatewayProtocolTrafficCaptureMiddleware>.Instance);
        await middleware.InvokeAsync(context, journal,
            new GatewayApplicationOptions("gateway@test", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)),
            environment).ConfigureAwait(false);
        Assert.AreEqual(500, context.Response.StatusCode);
        Assert.HasCount(2, journal.Records);
        var body = Encoding.UTF8.GetString(output.ToArray());
        Assert.IsFalse(body.Contains("partial-business-secret", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("journal unavailable", StringComparison.Ordinal));
        using var outbound = JsonDocument.Parse(journal.Records[1].Payload);
        Assert.AreEqual(500, outbound.RootElement.GetProperty("status").GetInt32());
        Assert.AreEqual(body, Encoding.UTF8.GetString(Convert.FromBase64String(
            outbound.RootElement.GetProperty("bodyBase64").GetString()!)), StringComparer.Ordinal);
    }

    private sealed class LimitProbeStream : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsLessThanOrEqualTo(65_537, BytesRead + buffer.Length, "Capture read beyond the detection byte.");
            buffer.Span.Fill(65);
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }
    }

    private sealed class StubTrafficJournal : IGatewayTrafficJournal
    {
        public List<GatewayTrafficRecord> Records { get; } = [];
        public string? FailedDirection { get; init; }

        public Task AppendAsync(
            GatewayTrafficRecord record,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(record.Direction, FailedDirection, StringComparison.Ordinal))
                throw new IOException("Injected journal failure.");
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GatewayTrafficRecord>>(
                Records.Where(record => record.SessionId == sessionId).ToArray());
    }
}
