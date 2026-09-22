using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class ApplicationRequestDispatcherTests
{
    [TestMethod]
    public async Task PingReturnsAJsonResponseWithoutAnyPresentationDependency()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new ApplicationRequestDispatcher(services);
        var request = NewRequest(ApplicationOperations.SystemPing, "{}"u8.ToArray());

        var response = await dispatcher.DispatchAsync(request);

        Assert.AreEqual(request.Id, response.RequestId);
        Assert.AreEqual("application/json", response.ContentType);
        Assert.IsFalse(response.IsError);
        var value = JsonSerializer.Deserialize<SystemPingResult>(
            response.Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsNotNull(value);
        Assert.IsTrue(value.RespondedAt <= DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public async Task UnknownOperationReturnsAStableApplicationError()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new ApplicationRequestDispatcher(services);
        var request = NewRequest("unknown.operation", "{}"u8.ToArray());

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsTrue(response.IsError);
        Assert.AreEqual("unknown-operation", response.ErrorCode);
        Assert.AreEqual("application/problem+json", response.ContentType);
    }

    private static ApplicationRequest NewRequest(string operation, byte[] payload)
    {
        var now = DateTimeOffset.UtcNow;
        return new ApplicationRequest(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            "admin",
            operation,
            "application/json",
            payload,
            new Dictionary<string, string>(),
            now,
            now.AddMinutes(1));
    }
}
