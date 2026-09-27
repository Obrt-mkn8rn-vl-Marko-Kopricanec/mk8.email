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
        Assert.AreEqual(DistributedContractVersions.Current, value.ContractVersion);
    }

    [TestMethod]
    [DataRow("unknown.operation")]
    [DataRow("smtp.unknown")]
    [DataRow("sieve.unknown")]
    [DataRow("pop3.unknown")]
    [DataRow("imap.unknown")]
    [DataRow("imap.mailboxes.unknown")]
    [DataRow("imap.messages.unknown")]
    [DataRow("admin.unknown")]
    [DataRow("oauth.unknown")]
    [DataRow("jmap.unknown")]
    [DataRow("dav.unknown")]
    public async Task UnknownOperationReturnsAStableApplicationError(string operation)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new ApplicationRequestDispatcher(services);
        var request = NewRequest(operation, "{}"u8.ToArray());

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsTrue(response.IsError);
        Assert.AreEqual("unknown-operation", response.ErrorCode);
        Assert.AreEqual("application/problem+json", response.ContentType);
    }

    [TestMethod]
    public async Task MalformedDelegatedPayloadReturnsInvalidArguments()
    {
        await using var services = new ServiceCollection()
            .AddSingleton<IJmapApplicationService>(new StubJmapApplicationService())
            .BuildServiceProvider();
        var dispatcher = new ApplicationRequestDispatcher(services);
        var request = NewRequest(ApplicationOperations.JmapBatchExecute, "{"u8.ToArray());

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsTrue(response.IsError);
        Assert.AreEqual("invalid-arguments", response.ErrorCode);
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

    [TestMethod]
    public async Task LegacyRawJmapOperationFailsClosed()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var response = await new ApplicationRequestDispatcher(services).DispatchAsync(
            NewRequest("jmap.api.process", "{\"document\":\"e30=\"}"u8.ToArray()));

        Assert.IsTrue(response.IsError);
        Assert.AreEqual("unknown-operation", response.ErrorCode);
    }

    [TestMethod]
    [DataRow("jmap.session.get")]
    [DataRow("jmap.event.poll")]
    [DataRow("jmap.batch.execute")]
    [DataRow("jmap.batch.execute.v2")]
    public async Task SupersededPresentationContractsFailClosed(string operation)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var result = await new ApplicationRequestDispatcher(services).DispatchAsync(NewRequest(operation, "{}"u8.ToArray()));
        Assert.IsTrue(result.IsError);
        Assert.AreEqual("unknown-operation", result.ErrorCode);
    }

    [TestMethod]
    public async Task JmapBatchDispatchesTypedInvocationsAndResults()
    {
        var service = new StubJmapApplicationService();
        await using var services = new ServiceCollection()
            .AddSingleton<IJmapApplicationService>(service)
            .BuildServiceProvider();
        var dispatcher = new ApplicationRequestDispatcher(services);
        var value = new JmapBatchApplicationRequest(
            new ProtocolAuthentication(
                ProtocolAuthenticationKinds.Password,
                "person@example.test",
                "secret"),
            new JmapApplicationBatch(["urn:ietf:params:jmap:core"],
                [new JmapApplicationCall(MailOperationKind.Echo, new System.Text.Json.Nodes.JsonObject
                {
                    ["ok"] = true, ["x"] = 1, ["X"] = 2,
                    ["nested"] = new System.Text.Json.Nodes.JsonObject { ["key"] = 3, ["Key"] = 4 },
                }, "c1")],
                new Dictionary<string, string> { ["created"] = "object-id" }));
        var request = NewRequest(
            ApplicationOperations.JmapBatchExecute,
            JsonSerializer.SerializeToUtf8Bytes(
                value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsFalse(response.IsError);
        Assert.AreEqual(value.Authentication.Username, service.Request?.Authentication.Username);
        Assert.IsNotNull(service.Request?.Batch);
        CollectionAssert.AreEqual(value.Batch!.Capabilities, service.Request.Batch.Capabilities);
        Assert.AreEqual(MailOperationKind.Echo, service.Request.Batch.Invocations[0].Operation);
        Assert.AreEqual("c1", service.Request.Batch.Invocations[0].CorrelationId);
        Assert.IsTrue(service.Request.Batch.Invocations[0].Arguments["ok"]!.GetValue<bool>());
        Assert.AreEqual(1, service.Request.Batch.Invocations[0].Arguments["x"]!.GetValue<int>());
        Assert.AreEqual(2, service.Request.Batch.Invocations[0].Arguments["X"]!.GetValue<int>());
        Assert.AreEqual(3, service.Request.Batch.Invocations[0].Arguments["nested"]!["key"]!.GetValue<int>());
        Assert.AreEqual(4, service.Request.Batch.Invocations[0].Arguments["nested"]!["Key"]!.GetValue<int>());
        Assert.AreEqual("object-id", service.Request.Batch.CreatedIds?["created"]);
        var result = JsonSerializer.Deserialize<JmapApplicationResult>(
            response.Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false });
        Assert.AreEqual(JmapApplicationOutcomes.Ok, result?.Outcome);
        Assert.IsNotNull(result?.Batch);
        Assert.AreEqual("worker-person", result.Batch.Profile.Username);
        Assert.AreEqual(MailOperationKind.Echo, result.Batch.Invocations[0].Operation);
        Assert.IsTrue(result.Batch.Invocations[0].Arguments["ok"]!.GetValue<bool>());
        Assert.AreEqual(1, result.Batch.Invocations[0].Arguments["x"]!.GetValue<int>());
        Assert.AreEqual(2, result.Batch.Invocations[0].Arguments["X"]!.GetValue<int>());
        Assert.IsNull(result.Content);
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

    private sealed class StubJmapApplicationService : IJmapApplicationService
    {
        public JmapBatchApplicationRequest? Request { get; private set; }

        public Task<JmapApplicationResult> GetProfileAsync(
            JmapProfileApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JmapApplicationResult> ExecuteBatchAsync(
            JmapBatchApplicationRequest request,
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                Batch: new JmapApplicationBatchResult(request.Batch!.Invocations.Select(call =>
                    new JmapApplicationInvocation(call.Operation, call.Arguments, call.CorrelationId)).ToArray(),
                    new JmapApplicationProfile("worker-person",
                        new JmapServiceLimits(10000, 1, 10000, 1, 64, 500, 500, 32, 255, 10000,
                            ["i;ascii-numeric"], ["receivedAt"]), []))));
        }

        public Task<JmapApplicationResult> UploadAsync(
            JmapUploadApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JmapApplicationResult> DownloadAsync(
            JmapDownloadApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JmapApplicationResult> PollChangesAsync(
            JmapChangesApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
