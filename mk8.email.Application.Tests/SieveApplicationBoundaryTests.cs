using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Sieve;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Sieve;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class SieveApplicationBoundaryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task GatewaySieveOperationsCrossTypedApplicationBoundary()
    {
        var application = new RecordingApplication();
        await using var services = new ServiceCollection()
            .AddSingleton<ISieveApplicationService>(application)
            .BuildServiceProvider();
        var gateway = new GatewaySieveApplicationService(
            new InProcessTransport(new ApplicationRequestDispatcher(services)));
        var userId = Guid.CreateVersion7();

        Assert.AreEqual("user@example.test", (await gateway.AuthenticatePasswordAsync(
            new SievePasswordAuthentication("user@example.test", "secret"))).Username);
        Assert.AreEqual("user@example.test", (await gateway.AuthenticateOAuthAsync(
            new SieveOAuthAuthentication("access-token"))).Username);
        Assert.IsTrue((await gateway.CheckSpaceAsync(
            new SieveCheckSpaceRequest(userId, "primary", 5, 20))).Succeeded);
        Assert.AreEqual("primary", (await gateway.ListAsync(new SieveUserRequest(userId)))[0].Name);
        Assert.AreEqual("keep;", (await gateway.GetAsync(
            new SieveNamedRequest(userId, "primary"))).Script?.Content);
        Assert.IsNull((await gateway.GetAsync(
            new SieveNamedRequest(userId, "missing"))).Script);
        Assert.IsTrue((await gateway.PutAsync(
            new SievePutRequest(userId, "primary", "keep;", 20))).Succeeded);
        Assert.IsTrue((await gateway.SetActiveAsync(
            new SieveSetActiveRequest(userId, "primary"))).Succeeded);
        Assert.IsTrue((await gateway.RenameAsync(
            new SieveRenameRequest(userId, "primary", "renamed"))).Succeeded);
        Assert.IsTrue((await gateway.DeleteAsync(
            new SieveNamedRequest(userId, "renamed"))).Succeeded);
        var invalid = await gateway.ValidateAsync(new SieveValidationRequest("invalid"));
        Assert.IsFalse(invalid.Succeeded);
        Assert.AreEqual(2, invalid.Diagnostic?.Line);
        Assert.AreEqual("keep;", application.LastPut?.Content);
        Assert.AreEqual("access-token", application.LastToken);
    }

    private sealed class InProcessTransport(ApplicationRequestDispatcher dispatcher)
        : IGatewayApplicationTransport
    {
        public async Task<TResponse> SendAsync<TRequest, TResponse>(
            string protocol,
            string operation,
            TRequest value,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("sieve", protocol);
            var now = DateTimeOffset.UtcNow;
            var request = new ApplicationRequest(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                0,
                protocol,
                operation,
                "application/json",
                JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
                new Dictionary<string, string>(),
                now,
                now.AddMinutes(1));
            var response = await dispatcher.DispatchAsync(request, cancellationToken);
            Assert.IsFalse(response.IsError, response.ErrorCode);
            return JsonSerializer.Deserialize<TResponse>(response.Payload, JsonOptions)
                ?? throw new InvalidOperationException("The application response was empty.");
        }
    }

    private sealed class RecordingApplication : ISieveApplicationService
    {
        public SievePutRequest? LastPut { get; private set; }
        public string? LastToken { get; private set; }

        public Task<SieveIdentityResult> AuthenticatePasswordAsync(
            SievePasswordAuthentication request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SieveIdentityResult(Guid.CreateVersion7(), request.Username));

        public Task<SieveIdentityResult> AuthenticateOAuthAsync(
            SieveOAuthAuthentication request,
            CancellationToken cancellationToken = default)
        {
            LastToken = request.AccessToken;
            return Task.FromResult(new SieveIdentityResult(Guid.CreateVersion7(), "user@example.test"));
        }

        public Task<SieveScriptOperationResult> CheckSpaceAsync(
            SieveCheckSpaceRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SieveScriptOperationResult(true));

        public Task<IReadOnlyList<SieveScriptSummary>> ListAsync(
            SieveUserRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SieveScriptSummary>>(
                [new SieveScriptSummary("primary", false, DateTime.UnixEpoch, DateTime.UnixEpoch)]);

        public Task<SieveStoredScriptResult> GetAsync(
            SieveNamedRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SieveStoredScriptResult(request.Name == "missing"
                ? null
                : new StoredSieveScript(
                    request.Name, "keep;", false, DateTime.UnixEpoch, DateTime.UnixEpoch)));

        public Task<SieveScriptOperationResult> PutAsync(
            SievePutRequest request,
            CancellationToken cancellationToken = default)
        {
            LastPut = request;
            return Task.FromResult(new SieveScriptOperationResult(true));
        }

        public Task<SieveScriptOperationResult> SetActiveAsync(
            SieveSetActiveRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SieveScriptOperationResult(true));

        public Task<SieveScriptOperationResult> DeleteAsync(
            SieveNamedRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SieveScriptOperationResult(true));

        public Task<SieveScriptOperationResult> RenameAsync(
            SieveRenameRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SieveScriptOperationResult(true));

        public Task<SieveValidationResult> ValidateAsync(
            SieveValidationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SieveValidationResult(
                false, new SieveDiagnosticResult(2, 3, "Invalid Sieve script.")));
    }
}
