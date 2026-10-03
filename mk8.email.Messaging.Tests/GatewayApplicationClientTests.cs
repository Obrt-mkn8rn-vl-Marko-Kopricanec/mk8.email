using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Configuration;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.OAuth;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class GatewayApplicationClientTests
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
            new Dictionary<string, string>(StringComparer.Ordinal)));
        var journal = new StubTrafficJournal();
        var client = CreateClient(requests, journal);

        var result = await client.AuthenticateAsync(
            new LoginRequestDTO("admin@example.test", "not-logged-secret")).ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(ApplicationOperations.AdminAuthenticate, requests.Request?.Operation, StringComparer.Ordinal);
        Assert.AreEqual("admin", requests.Request?.Protocol, StringComparer.Ordinal);
        Assert.IsFalse(requests.Request?.Metadata.Values.Contains("not-logged-secret", StringComparer.Ordinal) == true);
        Assert.HasCount(2, journal.Records);
        Assert.AreEqual(GatewayTrafficDirections.Inbound, journal.Records[0].Direction, StringComparer.Ordinal);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, journal.Records[1].Direction, StringComparer.Ordinal);
        Assert.AreEqual(journal.Records[0].SessionId, journal.Records[1].SessionId);
        Assert.AreEqual(requests.Request?.Id, journal.Records[0].ApplicationRequestId);
        StringAssert.Contains(
            Encoding.UTF8.GetString(journal.Records[0].Payload),
            "not-logged-secret", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ApplicationErrorIsJournaledAndRejectedAtPresentationBoundary()
    {
        var requests = new StubRequestClient(request => new ApplicationResponse(
            request.Id,
            "application/problem+json",
            "{}"u8.ToArray(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            IsError: true,
            ErrorCode: "invalid-arguments",
            ErrorDetail: "The request is invalid."));
        var journal = new StubTrafficJournal();
        var client = CreateClient(requests, journal);

        var exception = await Assert.ThrowsExactlyAsync<GatewayApplicationException>(
            () => client.GetDomainsAsync()).ConfigureAwait(false);

        Assert.AreEqual("invalid-arguments", exception.Code, StringComparer.Ordinal);
        Assert.HasCount(2, journal.Records);
        Assert.AreEqual("application/problem+json", journal.Records[1].ContentType, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task TransportFailureIsJournaledAndReportedAsUnavailable()
    {
        var requests = new StubRequestClient(_ =>
            throw new InvalidOperationException("simulated transport failure"));
        var journal = new StubTrafficJournal();
        var client = CreateClient(requests, journal);

        var exception = await Assert.ThrowsExactlyAsync<GatewayApplicationException>(
            () => client.GetDashboardAsync()).ConfigureAwait(false);

        Assert.AreEqual("application-transport-failure", exception.Code, StringComparer.Ordinal);
        Assert.IsTrue(exception.IsUnavailable);
        Assert.HasCount(2, journal.Records);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, journal.Records[1].Direction, StringComparer.Ordinal);
        StringAssert.Contains(
            Encoding.UTF8.GetString(journal.Records[1].Payload),
            "application-transport-failure", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DashboardCombinesApplicationDataWithGatewayLocalHealthSnapshot()
    {
        var directory = Directory.CreateTempSubdirectory("mk8-gateway-dashboard-");
        try
        {
            var statusPath = Path.Combine(directory.FullName, "status.json");
            await File.WriteAllTextAsync(statusPath,
                """
                {"state":"healthy","checkedAt":"2026-09-23T12:00:00Z",
                 "queueCount":0,"errorCount":0}
                """).ConfigureAwait(false);
            var expected = new AdminDashboardDTO([], [], MailSystemStatusDTO.Unavailable);
            var requests = new StubRequestClient(request => new ApplicationResponse(
                request.Id,
                "application/json",
                JsonSerializer.SerializeToUtf8Bytes(expected, JsonOptions),
                new Dictionary<string, string>(StringComparer.Ordinal)));
            var journal = new StubTrafficJournal();
            var client = CreateClient(requests, journal, statusPath);

            var result = await client.GetDashboardAsync().ConfigureAwait(false);

            Assert.AreEqual(ApplicationOperations.AdminDashboardGet, requests.Request?.Operation, StringComparer.Ordinal);
            Assert.AreEqual("healthy", result.SystemStatus.State, StringComparer.Ordinal);
            Assert.HasCount(0, result.Domains);
            Assert.HasCount(0, result.Accounts);
            Assert.HasCount(2, journal.Records);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task OAuthClientUsesTypedOperationAndOAuthTrafficJournal()
    {
        var expected = new OAuthPublicKeyValue(
            "RSA",
            "sig",
            "key-1",
            "RS256",
            "modulus",
            "AQAB");
        var requests = new StubRequestClient(request => new ApplicationResponse(
            request.Id,
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(expected, JsonOptions),
            new Dictionary<string, string>(StringComparer.Ordinal)));
        var journal = new StubTrafficJournal();
        var transport = new GatewayApplicationTransport(requests, journal, TestOptions());
        var client = new GatewayOAuthClient(transport);

        var result = await client.GetPublicKeyAsync().ConfigureAwait(false);

        Assert.AreEqual(expected, result);
        Assert.AreEqual("oauth", requests.Request?.Protocol, StringComparer.Ordinal);
        Assert.AreEqual(ApplicationOperations.OAuthPublicKeyGet, requests.Request?.Operation, StringComparer.Ordinal);
        Assert.HasCount(2, journal.Records);
        Assert.IsTrue(journal.Records.All(record => string.Equals(record.Protocol, "oauth", StringComparison.Ordinal)));
    }

    private static GatewayApplicationOptions TestOptions() => new(
        "gateway@test-host",
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10));

    [TestMethod]
    public async Task OnePresentationDeadlineClipsEveryDurableOperationAndNestedScopes()
    {
        var requests = new StubRequestClient(request => new ApplicationResponse(request.Id, "application/json",
            "true"u8.ToArray(), new Dictionary<string, string>(StringComparer.Ordinal)));
        var journal = new StubTrafficJournal();
        var transport = new GatewayApplicationTransport(requests, journal, TestOptions());
        DateTimeOffset bounded;
        using (GatewayApplicationDeadline.Begin(TimeSpan.FromSeconds(3)))
        {
            bounded = GatewayApplicationDeadline.Clip(DateTimeOffset.MaxValue);
            await transport.SendAsync<object, bool>("jmap", ApplicationOperations.MailPlanValidate, new { }).ConfigureAwait(false);
            Assert.AreEqual(bounded, requests.Request!.Deadline);
            using (GatewayApplicationDeadline.Begin(TimeSpan.FromSeconds(20)))
                Assert.AreEqual(bounded, GatewayApplicationDeadline.Clip(DateTimeOffset.MaxValue));
            await transport.SendAsync<object, bool>("jmap", ApplicationOperations.MailOperationExecute, new { }).ConfigureAwait(false);
            Assert.AreEqual(bounded, requests.Request!.Deadline);
        }
        Assert.AreEqual(DateTimeOffset.MaxValue, GatewayApplicationDeadline.Clip(DateTimeOffset.MaxValue));
        Assert.HasCount(4, journal.Records);
    }

    [TestMethod]
    public async Task ExpiredPresentationDeadlineJournalsRejectionWithoutQueuingWork()
    {
        var requests = new StubRequestClient(_ => throw new AssertFailedException("Expired work was queued."));
        var journal = new StubTrafficJournal();
        var transport = new GatewayApplicationTransport(requests, journal, TestOptions());
        using var deadline = GatewayApplicationDeadline.Begin(TimeSpan.FromSeconds(-1));
        var failure = await Assert.ThrowsExactlyAsync<GatewayApplicationException>(
            () => transport.SendAsync<object, bool>("jmap", ApplicationOperations.MailOperationExecute, new { })).ConfigureAwait(false);
        Assert.AreEqual("application-timeout", failure.Code, StringComparer.Ordinal);
        Assert.IsTrue(failure.IsUnavailable);
        Assert.IsNull(requests.Request);
        Assert.HasCount(2, journal.Records);
        StringAssert.Contains(Encoding.UTF8.GetString(journal.Records[1].Payload), "application-timeout", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ParallelPresentationDeadlineScopesAreIsolated()
    {
        async Task<DateTimeOffset> ScopedAsync(int seconds)
        {
            using var scope = GatewayApplicationDeadline.Begin(TimeSpan.FromSeconds(seconds));
            await Task.Yield();
            return GatewayApplicationDeadline.Clip(DateTimeOffset.MaxValue);
        }
        var values = await Task.WhenAll(ScopedAsync(2), ScopedAsync(20)).ConfigureAwait(false);
        Assert.IsTrue(values[1] - values[0] > TimeSpan.FromSeconds(15));
        Assert.AreEqual(DateTimeOffset.MaxValue, GatewayApplicationDeadline.Clip(DateTimeOffset.MaxValue));
    }

    private static GatewayApplicationClient CreateClient(
        IApplicationRequestClient requests,
        IGatewayTrafficJournal journal,
        string? statusPath = null) =>
        new(
            new GatewayApplicationTransport(requests, journal, TestOptions()),
            new GatewayMailSystemStatusReader(
                new EnvironmentConfig
                {
                    Admin = new AdminConfig
                    {
                        HealthStatusPath = statusPath ?? "gateway-status-unavailable.json",
                    },
                },
                NullLogger<GatewayMailSystemStatusReader>.Instance));

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
