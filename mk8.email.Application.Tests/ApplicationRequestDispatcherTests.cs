using System.Text.Json;
using System.Text.Json.Nodes;
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
    [DataRow("jmap.profile.get")]
    [DataRow("jmap.batch.execute.v5")]
    [DataRow("jmap.upload")]
    [DataRow("jmap.download")]
    [DataRow("jmap.changes.poll")]
    [DataRow("mail.operation.execute")]
    [DataRow("mail.operation.execute.v2")]
    [DataRow("mail.operation.execute.v3")]
    [DataRow("mail.operation.execute.v4")]
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
        var request = NewRequest(ApplicationOperations.MailOperationExecute, "{"u8.ToArray());

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsTrue(response.IsError);
        Assert.AreEqual("invalid-arguments", response.ErrorCode);
        Assert.AreEqual("application/problem+json", response.ContentType);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyOrWrongCaseReferenceAliasEnvelopeFailsBeforeDispatch(bool wrongCase)
    {
        var service = new StubJmapApplicationService();
        await using var services = new ServiceCollection()
            .AddSingleton<IJmapApplicationService>(service)
            .BuildServiceProvider();
        var value = new MailOperationApplicationRequest(
            new(ProtocolAuthenticationKinds.Password, "person@example.test", "secret"),
            new([MailFeature.Basic], MailOperationKind.ReadFolders, new JsonObject(),
                new Dictionary<string, string>(StringComparer.Ordinal)));
        var payload = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
        var command = payload["command"]!.AsObject();
        command.Remove("referenceAliases");
        if (wrongCase)
            command["ReferenceAliases"] = new JsonObject();

        var response = await new ApplicationRequestDispatcher(services).DispatchAsync(
            NewRequest(ApplicationOperations.MailOperationExecute,
                JsonSerializer.SerializeToUtf8Bytes(payload)));

        Assert.IsTrue(response.IsError);
        Assert.AreEqual("invalid-arguments", response.ErrorCode);
        Assert.IsNull(service.Request);
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
    public async Task MailOperationDispatchesResolvedArgumentsAndLosslessResults()
    {
        var service = new StubJmapApplicationService();
        await using var services = new ServiceCollection().AddSingleton<IJmapApplicationService>(service).BuildServiceProvider();
        var dispatcher = new ApplicationRequestDispatcher(services);
        var value = new MailOperationApplicationRequest(new(ProtocolAuthenticationKinds.Password, "person@example.test", "secret"),
            new([MailFeature.Basic], MailOperationKind.ReadFolders, new System.Text.Json.Nodes.JsonObject
            {
                ["ok"] = true,
                ["x"] = 1,
                ["X"] = 2,
                ["nested"] = new System.Text.Json.Nodes.JsonObject { ["key"] = 3, ["Key"] = 4 },
            }, new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string> { ["created"] = "object-id" }));
        var request = NewRequest(ApplicationOperations.MailOperationExecute,
            JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var response = await dispatcher.DispatchAsync(request);
        Assert.IsFalse(response.IsError);
        Assert.AreEqual(value.Authentication.Username, service.Request?.Authentication.Username);
        Assert.AreEqual(request.Id, service.OperationId);
        Assert.IsNotNull(service.Request?.Command);
        CollectionAssert.AreEqual(value.Command.Features.ToArray(), service.Request.Command.Features.ToArray());
        Assert.AreEqual(MailOperationKind.ReadFolders, service.Request.Command.Operation);
        Assert.IsTrue(service.Request.Command.Arguments["ok"]!.GetValue<bool>());
        Assert.AreEqual(1, service.Request.Command.Arguments["x"]!.GetValue<int>());
        Assert.AreEqual(2, service.Request.Command.Arguments["X"]!.GetValue<int>());
        Assert.AreEqual(3, service.Request.Command.Arguments["nested"]!["key"]!.GetValue<int>());
        Assert.AreEqual(4, service.Request.Command.Arguments["nested"]!["Key"]!.GetValue<int>());
        Assert.AreEqual("object-id", service.Request.Command.KnownEntities?["created"]);
        Assert.AreEqual(0, service.Request.Command.ReferenceAliases.Count);
        var result = JsonSerializer.Deserialize<JmapApplicationResult>(response.Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false });
        Assert.AreEqual(JmapApplicationOutcomes.Ok, result?.Outcome);
        Assert.IsNotNull(result?.OperationResult);
        Assert.AreEqual("worker-person", result.OperationResult.Profile.Username);
        Assert.AreEqual(MailOperationKind.ReadFolders, result.OperationResult.Response.Operation);
        var data = ApplicationValueCodec.Decode(result.OperationResult.Response.Data)!;
        Assert.IsTrue(data["ok"]!.GetValue<bool>());
        Assert.AreEqual(1, data["x"]!.GetValue<int>());
        Assert.AreEqual(2, data["X"]!.GetValue<int>());
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
        public MailOperationApplicationRequest? Request { get; private set; }

        public Task<JmapApplicationResult> GetProfileAsync(
            JmapProfileApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Guid OperationId { get; private set; }

        public Task<JmapApplicationResult> ValidatePlanAsync(MailPlanApplicationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JmapApplicationResult> ExecuteOperationAsync(
            MailOperationApplicationRequest request, Guid operationId, CancellationToken cancellationToken = default)
        {
            Request = request;
            OperationId = operationId;
            return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                OperationResult: new(new(request.Command.Operation, ApplicationValueCodec.Encode(request.Command.Arguments)),
                    request.Command.KnownEntities!, new JmapApplicationProfile("worker-person",
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
