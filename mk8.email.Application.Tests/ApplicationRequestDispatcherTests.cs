using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
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

    [TestMethod]
    public async Task OAuthAuthorizationDispatchesWithoutAnyPresentationType()
    {
        var service = new StubOAuthApplicationService();
        await using var services = new ServiceCollection()
            .AddSingleton<IOAuthApplicationService>(service)
            .BuildServiceProvider();
        var dispatcher = new ApplicationRequestDispatcher(services);
        var value = new OAuthAuthorizeApplicationRequest(
            "person@example.test",
            "secret",
            "123456",
            "thunderbird",
            "http://127.0.0.1:49152/",
            "Test device",
            ["offline_access", "imap"],
            new string('a', 43),
            null);
        var request = NewRequest(
            ApplicationOperations.OAuthAuthorize,
            JsonSerializer.SerializeToUtf8Bytes(
                value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsFalse(response.IsError);
        Assert.AreEqual(value.Username, service.Request?.Username);
        Assert.AreEqual(value.ClientId, service.Request?.ClientId);
        CollectionAssert.AreEqual(value.Scopes.ToArray(), service.Request?.Scopes.ToArray());
        var result = JsonSerializer.Deserialize<OAuthAuthorizeApplicationResult>(
            response.Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.AreEqual(OAuthAuthorizationOutcome.Succeeded, result?.Outcome);
        Assert.AreEqual("authorization-code", result?.AuthorizationCode);
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

    private sealed class StubOAuthApplicationService : IOAuthApplicationService
    {
        public OAuthAuthorizeApplicationRequest? Request { get; private set; }

        public Task<OAuthPublicKeyValue> GetPublicKeyAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthIdentityLookupResult> AuthenticateIdentityAsync(
            OAuthIdentityLookupRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthAuthorizeApplicationResult> AuthorizeAsync(
            OAuthAuthorizeApplicationRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new OAuthAuthorizeApplicationResult(
                OAuthAuthorizationOutcome.Succeeded,
                "authorization-code"));
        }

        public Task<OAuthTokenApplicationResult> RedeemAuthorizationCodeAsync(
            OAuthAuthorizationCodeRedeemRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthTokenApplicationResult> RefreshTokenAsync(
            OAuthRefreshTokenRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeTokenAsync(
            OAuthRevokeTokenRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
