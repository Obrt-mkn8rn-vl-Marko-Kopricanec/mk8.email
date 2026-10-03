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

    private sealed class StubTrafficJournal : IGatewayTrafficJournal
    {
        public List<GatewayTrafficRecord> Records { get; } = [];

        public Task AppendAsync(
            GatewayTrafficRecord record,
            CancellationToken cancellationToken = default)
        {
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
