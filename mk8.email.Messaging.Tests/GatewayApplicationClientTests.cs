using System.Text;
using System.Text.Json;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Messaging.Tests;

[TestClass]
public sealed class GatewayApplicationClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task AuthenticationUsesDurableTypedRequestAndBidirectionalJournal()
    {
        var expected = new LoginResultDTO(
            true,
            new UserDTO(
                Guid.CreateVersion7(),
                "admin@example.test",
                UserRole.SuperAdmin,
                null,
                true,
                DateTime.UtcNow),
            null);
        var requests = new StubRequestClient(request => new ApplicationResponse(
            request.Id,
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(expected, JsonOptions),
            new Dictionary<string, string>()));
        var journal = new StubTrafficJournal();
        var client = new GatewayApplicationClient(
            requests,
            journal,
            TestOptions());

        var result = await client.AuthenticateAsync(
            new LoginRequestDTO("admin@example.test", "not-logged-secret"));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(ApplicationOperations.AdminAuthenticate, requests.Request?.Operation);
        Assert.AreEqual("admin", requests.Request?.Protocol);
        Assert.IsFalse(requests.Request?.Metadata.Values.Contains("not-logged-secret") == true);
        Assert.HasCount(2, journal.Records);
        Assert.AreEqual(GatewayTrafficDirections.Inbound, journal.Records[0].Direction);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, journal.Records[1].Direction);
        Assert.AreEqual(journal.Records[0].SessionId, journal.Records[1].SessionId);
        Assert.AreEqual(requests.Request?.Id, journal.Records[0].ApplicationRequestId);
        StringAssert.Contains(
            Encoding.UTF8.GetString(journal.Records[0].Payload),
            "not-logged-secret");
    }

    [TestMethod]
    public async Task ApplicationErrorIsJournaledAndRejectedAtPresentationBoundary()
    {
        var requests = new StubRequestClient(request => new ApplicationResponse(
            request.Id,
            "application/problem+json",
            "{}"u8.ToArray(),
            new Dictionary<string, string>(),
            IsError: true,
            ErrorCode: "invalid-arguments",
            ErrorDetail: "The request is invalid."));
        var journal = new StubTrafficJournal();
        var client = new GatewayApplicationClient(
            requests,
            journal,
            TestOptions());

        var exception = await Assert.ThrowsExactlyAsync<GatewayApplicationException>(
            () => client.GetDomainsAsync());

        Assert.AreEqual("invalid-arguments", exception.Code);
        Assert.HasCount(2, journal.Records);
        Assert.AreEqual("application/problem+json", journal.Records[1].ContentType);
    }

    [TestMethod]
    public async Task TransportFailureIsJournaledAndReportedAsUnavailable()
    {
        var requests = new StubRequestClient(_ =>
            throw new InvalidOperationException("simulated transport failure"));
        var journal = new StubTrafficJournal();
        var client = new GatewayApplicationClient(requests, journal, TestOptions());

        var exception = await Assert.ThrowsExactlyAsync<GatewayApplicationException>(
            () => client.GetDashboardAsync());

        Assert.AreEqual("application-transport-failure", exception.Code);
        Assert.IsTrue(exception.IsUnavailable);
        Assert.HasCount(2, journal.Records);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, journal.Records[1].Direction);
        StringAssert.Contains(
            Encoding.UTF8.GetString(journal.Records[1].Payload),
            "application-transport-failure");
    }

    private static GatewayApplicationOptions TestOptions() => new(
        "gateway@test-host",
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10));

    private sealed class StubRequestClient(
        Func<ApplicationRequest, ApplicationResponse> responseFactory) : IApplicationRequestClient
    {
        public ApplicationRequest? Request { get; private set; }

        public Task EnqueueAsync(
            ApplicationRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.CompletedTask;
        }

        public Task<ApplicationResponse> WaitForResponseAsync(
            Guid requestId,
            DateTimeOffset deadline,
            CancellationToken cancellationToken = default) =>
            Request is null
                ? throw new InvalidOperationException("No request was recorded.")
                : Task.FromResult(responseFactory(Request));

        public Task<ApplicationResponse> SendAsync(
            ApplicationRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(responseFactory(request));
        }

        public Task<ApplicationExchangeSnapshot?> GetAsync(
            Guid requestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ApplicationExchangeSnapshot?>(null);
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
