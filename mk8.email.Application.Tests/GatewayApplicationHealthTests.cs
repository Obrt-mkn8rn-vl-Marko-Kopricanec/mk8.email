using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class GatewayApplicationHealthTests
{
    [TestMethod]
    public async Task ApplicationHealthUsesWorkerPingThroughTypedTransport()
    {
        var responseTime = DateTimeOffset.UtcNow;
        var transport = new StubTransport(responseTime);
        var result = await GatewayApplicationHealth.CheckAsync(transport, CancellationToken.None).ConfigureAwait(false);
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.AreEqual("health", transport.Protocol, StringComparer.Ordinal);
        Assert.AreEqual(ApplicationOperations.SystemPing, transport.Operation, StringComparer.Ordinal);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body).ConfigureAwait(false);
        Assert.AreEqual("ready", document.RootElement.GetProperty("status").GetString(), StringComparer.Ordinal);
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
                    httpContext.RequestAborted).ConfigureAwait(false);
                await result.ExecuteAsync(httpContext).ConfigureAwait(false);
            },
            NullLogger<GatewayApplicationFailureMiddleware>.Instance);

        await middleware.InvokeAsync(context).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.AreEqual("5", context.Response.Headers.RetryAfter.ToString(), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("mk8.distributed.v3")]
    [DataRow("different-contract")]
    public async Task IncompatibleWorkerCannotProduceAReadyHealthResponse(string? version)
    {
        var result = await GatewayApplicationHealth.CheckAsync(new StubTransport(DateTimeOffset.UtcNow, version), CancellationToken.None).ConfigureAwait(false);
        var context = new DefaultHttpContext();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var body = new MemoryStream();
        context.RequestServices = services;
        context.Response.Body = body;
        await result.ExecuteAsync(context).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    private sealed class StubTransport(DateTimeOffset responseTime, string? version = DistributedContractVersions.Current) : IGatewayApplicationTransport
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
            return Task.FromResult((TResponse)(object)new SystemPingResult(responseTime, version));
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
