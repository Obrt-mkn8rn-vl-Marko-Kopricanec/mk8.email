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
public sealed class GatewayApplicationFailureMiddlewareTests
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

        await middleware.InvokeAsync(context);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.AreEqual("5", context.Response.Headers.RetryAfter.ToString());
        context.Response.Body.Position = 0;
        var body = await new StreamReader(
            context.Response.Body,
            Encoding.UTF8).ReadToEndAsync();
        StringAssert.Contains(body, "temporarily_unavailable");
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
                await requestContext.Response.WriteAsync("{\"ok\":true}");
            },
            NullLogger<GatewayProtocolTrafficCaptureMiddleware>.Instance);

        await middleware.InvokeAsync(
            context,
            journal,
            new GatewayApplicationOptions(
                "gateway@test-host",
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10)),
            new EnvironmentConfig());

        Assert.HasCount(2, journal.Records);
        Assert.AreEqual(GatewayTrafficDirections.Inbound, journal.Records[0].Direction);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, journal.Records[1].Direction);
        Assert.AreEqual(journal.Records[0].SessionId, journal.Records[1].SessionId);
        using var inbound = JsonDocument.Parse(journal.Records[0].Payload);
        Assert.AreEqual("/oauth/token", inbound.RootElement.GetProperty("path").GetString());
        Assert.AreEqual(
            "grant_type=refresh_token&refresh_token=secret-value",
            Encoding.UTF8.GetString(Convert.FromBase64String(
                inbound.RootElement.GetProperty("bodyBase64").GetString()!)));
        context.Response.Body.Position = 0;
        Assert.AreEqual(
            "{\"ok\":true}",
            await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync());
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
                await requestContext.Response.StartAsync();
                await requestContext.Response.WriteAsync("event: ping\ndata: {\"interval\":15}\n\n");
                await requestContext.Response.WriteAsync("event: ping\ndata: {\"interval\":15}\n\n");
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
            });

        Assert.HasCount(4, journal.Records);
        Assert.AreEqual(GatewayTrafficDirections.Inbound, journal.Records[0].Direction);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, journal.Records[1].Direction);
        Assert.AreEqual("application/vnd.mk8.gateway-http+json", journal.Records[1].ContentType);
        Assert.AreEqual(
            "application/vnd.mk8.gateway-http-chunk+json",
            journal.Records[2].ContentType);
        Assert.AreEqual(
            "application/vnd.mk8.gateway-http-chunk+json",
            journal.Records[3].ContentType);
        Assert.IsTrue(journal.Records.All(record => record.Protocol == "jmap"));
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
