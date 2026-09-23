using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayApplicationHealthTests
{
    [TestMethod]
    public async Task ApplicationHealthUsesWorkerPingThroughTypedTransport()
    {
        var responseTime = DateTimeOffset.UtcNow;
        var transport = new StubTransport(responseTime);
        var result = await GatewayApplicationHealth.CheckAsync(transport, CancellationToken.None);
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.AreEqual("health", transport.Protocol);
        Assert.AreEqual(ApplicationOperations.SystemPing, transport.Operation);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.AreEqual("ready", document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            responseTime,
            document.RootElement.GetProperty("respondedAt").GetDateTimeOffset());
    }

    [TestMethod]
    public async Task MissingWorkerIsReportedAsUnavailableWithoutAFalseReadyResponse()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/health/application";
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        var middleware = new GatewayApplicationFailureMiddleware(
            async httpContext =>
            {
                var result = await GatewayApplicationHealth.CheckAsync(
                    new UnavailableTransport(),
                    httpContext.RequestAborted);
                await result.ExecuteAsync(httpContext);
            },
            NullLogger<GatewayApplicationFailureMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.AreEqual("5", context.Response.Headers.RetryAfter.ToString());
    }

    private sealed class StubTransport(DateTimeOffset responseTime) : IGatewayApplicationTransport
    {
        public string? Protocol { get; private set; }
        public string? Operation { get; private set; }

        public Task<TResponse> SendAsync<TRequest, TResponse>(
            string protocol,
            string operation,
            TRequest value,
            CancellationToken cancellationToken = default)
        {
            Protocol = protocol;
            Operation = operation;
            Assert.IsInstanceOfType<object>(value);
            return Task.FromResult((TResponse)(object)new SystemPingResult(responseTime));
        }
    }

    private sealed class UnavailableTransport : IGatewayApplicationTransport
    {
        public Task<TResponse> SendAsync<TRequest, TResponse>(
            string protocol,
            string operation,
            TRequest value,
            CancellationToken cancellationToken = default) =>
            throw new GatewayApplicationException(
                "application-timeout",
                "The worker is not available.",
                isUnavailable: true);
    }
}
