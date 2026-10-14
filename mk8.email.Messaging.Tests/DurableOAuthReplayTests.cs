using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Protocol;
using mk8.email.Contracts.Storage;
using mk8.email.Hosting;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers these finite real PostgreSQL/OAuth/receipt controls by reflection.")]
internal sealed class DurableOAuthReplayTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task ActualWorkerPreservesCommittedOAuthResultAcrossCompletionFault(bool redeemCode, bool acknowledged)
    {
        Assert.IsNotNull(TestContext);
        var token = TestContext.CancellationToken;
        var fixture = await Rig.CreateAsync(token).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var request = await fixture.CreateRequestAsync(redeemCode, token).ConfigureAwait(false);
        await fixture.Bus.EnqueueAsync(request, token).ConfigureAwait(false);
        var lease = await fixture.Bus.WaitForRequestAsync("worker:first", token).ConfigureAwait(false);
        var faulting = new CompletionFaultConsumer(fixture.Bus, acknowledged);
        await fixture.ProcessWithWorkerAsync(faulting, lease, token).ConfigureAwait(false);
        var first = faulting.DispatchedResponse;
        Assert.IsNotNull(first);
        Assert.AreEqual(1, faulting.CompletionCalls);
        Assert.AreEqual(0, faulting.FailureCalls);
        var issued = Decode(first).Token;
        Assert.IsNotNull(issued);
        await fixture.AssertEncryptedReceiptAsync(request.Id, issued, token).ConfigureAwait(false);
        var snapshot = await fixture.Bus.GetAsync(request.Id, token).ConfigureAwait(false);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(acknowledged ? "completed" : "processing", snapshot.State, StringComparer.Ordinal);
        if (!acknowledged)
        {
            await Task.Delay(TimeSpan.FromSeconds(6), token).ConfigureAwait(false);
            var reclaimed = await fixture.Bus.WaitForRequestAsync("worker:recovery", token).ConfigureAwait(false);
            Assert.AreEqual(request.Id, reclaimed.Request.Id);
            Assert.IsGreaterThan(lease.AttemptCount, reclaimed.AttemptCount);
            await fixture.ProcessWithWorkerAsync(fixture.Bus, reclaimed, token).ConfigureAwait(false);
        }
        var recovered = await fixture.Bus.WaitForResponseAsync(request.Id, request.Deadline, token).ConfigureAwait(false);
        Assert.AreSequenceEqual(first.Payload, recovered.Payload);
        Assert.IsNotNull(await fixture.AuthenticateAsync(issued.AccessToken, token).ConfigureAwait(false));
        var database = fixture.Context();
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync(token).ConfigureAwait(false));
        Assert.AreEqual(redeemCode ? 2 : 4, await database.OAuthTokens.CountAsync(token).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task ReclaimedDurableOperationReturnsEncryptedOriginalResultWithoutASecondCredentialUse(bool redeemCode)
    {
        Assert.IsNotNull(TestContext);
        var token = TestContext.CancellationToken;
        var fixture = await Rig.CreateAsync(token).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var request = await fixture.CreateRequestAsync(redeemCode, token).ConfigureAwait(false);
        await fixture.Bus.EnqueueAsync(request, token).ConfigureAwait(false);
        var firstLease = await fixture.Bus.WaitForRequestAsync("worker:first", token).ConfigureAwait(false);
        var first = await fixture.InvokeAsync(firstLease.Request, token).ConfigureAwait(false);
        var issued = Decode(first);
        Assert.IsNotNull(issued.Token);
        await fixture.AssertEncryptedReceiptAsync(request.Id, issued.Token, token).ConfigureAwait(false);
        // Intentionally omit bus completion AFTER the actual business+receipt
        // commit. This is a controlled lost-response boundary, not an OS crash.
        await Task.Delay(TimeSpan.FromSeconds(6), token).ConfigureAwait(false);
        var reclaimed = await fixture.Bus.WaitForRequestAsync("worker:recovery", token).ConfigureAwait(false);
        Assert.AreEqual(request.Id, reclaimed.Request.Id);
        Assert.IsGreaterThan(firstLease.AttemptCount, reclaimed.AttemptCount);
        var replay = await fixture.InvokeAsync(reclaimed.Request, token).ConfigureAwait(false);
        Assert.AreSequenceEqual(first.Payload, replay.Payload);
        await fixture.Bus.CompleteAsync(reclaimed, replay, token).ConfigureAwait(false);
        Assert.AreSequenceEqual(first.Payload, (await fixture.Bus.WaitForResponseAsync(request.Id, request.Deadline, token).ConfigureAwait(false)).Payload);
        Assert.IsNotNull(await fixture.AuthenticateAsync(issued.Token.AccessToken, token).ConfigureAwait(false));

        var changedPayload = ChangedClientPayload(request, redeemCode);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.InvokeAsync(
            request with { Payload = changedPayload }, token)).ConfigureAwait(false);
        Assert.IsNotNull(await fixture.AuthenticateAsync(issued.Token.AccessToken, token).ConfigureAwait(false));

        var secondUse = await fixture.InvokeAsync(request with { Id = Guid.CreateVersion7() }, token).ConfigureAwait(false);
        Assert.IsNull(Decode(secondUse).Token, "A DISTINCT operation must retain the ordinary single-use/replay defense.");
        if (redeemCode)
            Assert.IsNotNull(await fixture.AuthenticateAsync(issued.Token.AccessToken, token).ConfigureAwait(false));
        else
            Assert.IsNull(await fixture.AuthenticateAsync(issued.Token.AccessToken, token).ConfigureAwait(false));
        var database = fixture.Context();
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(1, await database.OAuthGrants.CountAsync(token).ConfigureAwait(false));
        Assert.AreEqual(redeemCode ? 2 : 4, await database.OAuthTokens.CountAsync(token).ConfigureAwait(false));
        await fixture.AssertReplayAfterCredentialRemovalAsync(request, redeemCode, first.Payload, token).ConfigureAwait(false);
    }

    private static OAuthTokenApplicationResult Decode(ApplicationResponse response) =>
        JsonSerializer.Deserialize<OAuthTokenApplicationResult>(response.Payload, JsonOptions)
        ?? throw new AssertFailedException("Missing actual OAuth result.");

    private static byte[] ChangedClientPayload(ApplicationRequest request, bool code)
    {
        if (code)
        {
            var input = JsonSerializer.Deserialize<OAuthAuthorizationCodeRedeemRequest>(request.Payload, JsonOptions);
            Assert.IsNotNull(input);
            return JsonSerializer.SerializeToUtf8Bytes(input with { ClientId = "different-client" }, JsonOptions);
        }
        var refresh = JsonSerializer.Deserialize<OAuthRefreshTokenRequest>(request.Payload, JsonOptions);
        Assert.IsNotNull(refresh);
        return JsonSerializer.SerializeToUtf8Bytes(refresh with { ClientId = "different-client" }, JsonOptions);
    }

    private sealed class Rig(PostgresTestDatabase server) : IAsyncDisposable
    {
        private readonly PostgresTestDatabase _server = server;
        private NpgsqlDataSource? _source;
        private ServiceProvider? _services;
        private AesGcmPayloadProtector? _protector;
        private PostgresApplicationBus? _bus;
        private readonly InMemoryLargeObjectStore objects = new();
        private readonly Guid userId = Guid.CreateVersion7();
        private ServiceProvider Services => _services ?? throw new InvalidOperationException("Fixture services are not initialized.");
        internal PostgresApplicationBus Bus => _bus ?? throw new InvalidOperationException("Fixture bus is not initialized.");
        internal EmailDbContext Context() => new(new DbContextOptionsBuilder<EmailDbContext>().UseNpgsql(_server.ConnectionString).Options);

        internal async Task ProcessWithWorkerAsync(IApplicationRequestConsumer consumer,
            ApplicationRequestLease lease, CancellationToken token)
        {
            using var worker = new ApplicationRequestWorker(consumer, Services.GetRequiredService<IServiceScopeFactory>(),
                new ApplicationWorkerIdentity(lease.WorkerId, TimeSpan.FromSeconds(1)),
                NullLogger<ApplicationRequestWorker>.Instance);
            await worker.ProcessLeaseAsync(lease, token).ConfigureAwait(false);
        }

        internal static async Task<Rig> CreateAsync(CancellationToken token)
        {
            var server = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
            if (server is null) Assert.Inconclusive("An owned PostgreSQL development fixture is required.");
            var fixture = new Rig(server); // Own the server before the next acquisition can fail.
            try
            {
                fixture.Initialize();
                var database = fixture.Context();
                await using var databaseLifetime = database.ConfigureAwait(false);
                await database.Database.EnsureCreatedAsync(token).ConfigureAwait(false);
                await new MailRuntimeSchemaService(database).EnsureAsync(token).ConfigureAwait(false);
                var company = new CompanyDB { Id = Guid.CreateVersion7(), Name = "Reserved OAuth fixture", IsActive = true };
                await database.Users.AddAsync(new UserDB { Id = fixture.userId, Username = "owner@example.test", PasswordHash = "unused", Company = company, IsActive = true }, token).ConfigureAwait(false);
                await database.Addresses.AddAsync(new AddressDB { Id = Guid.CreateVersion7(), Domain = "example.test", Company = company, IsActive = true }, token).ConfigureAwait(false);
                await database.SaveChangesAsync(token).ConfigureAwait(false);
                await PostgresMessagingSchema.EnsureAsync(fixture._source ?? throw new InvalidOperationException("Missing fixture source."), token).ConfigureAwait(false);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private void Initialize()
        {
            _source = NpgsqlDataSource.Create(_server.ConnectionString);
            _protector = AesGcmPayloadProtectorTests.CreateProtector("receipt", "synthetic-oauth-receipt-key");
            var environment = new EnvironmentConfig { OAuth = new OAuthConfig { ClientId = "fixture-client", EnableOpenIdConnect = false } };
            _services = new ServiceCollection().AddLogging().AddSingleton(environment)
                .AddSingleton<ILargeObjectStore>(objects)
                .AddSingleton<IStoredContentProtector>(new MessagingStoredContentProtector(_protector))
                .AddDbContext<EmailDbContext>(options => options.UseNpgsql(_server.ConnectionString))
                .AddApplication().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            _bus = new PostgresApplicationBus(_source, _protector, new PostgresMessagingOptions
            { LeaseDuration = TimeSpan.FromSeconds(5), NotificationFallbackInterval = TimeSpan.FromMilliseconds(100) });
        }

        internal async Task<ApplicationRequest> CreateRequestAsync(bool code, CancellationToken token)
        {
            var scope = Services.CreateAsyncScope();
            await using var scopeLifetime = scope.ConfigureAwait(false);
            byte[] payload;
            if (code)
            {
                var verifier = new string('v', 64);
                var value = await scope.ServiceProvider.GetRequiredService<IOAuthAuthorizationService>()
                    .CreateAuthorizationCodeAsync(userId, "fixture-client", "http://127.0.0.1:49152/", "fixture",
                        ["imap", "offline_access"], OAuthProtocolValues.CreatePkceChallenge(verifier), cancellationToken: token).ConfigureAwait(false);
                Assert.IsNotNull(value);
                payload = JsonSerializer.SerializeToUtf8Bytes(new OAuthAuthorizationCodeRedeemRequest(value,
                    "fixture-client", "http://127.0.0.1:49152/", verifier), JsonOptions);
            }
            else
            {
                var value = await scope.ServiceProvider.GetRequiredService<IOAuthTokenService>()
                    .CreateGrantAsync(userId, "fixture-client", "fixture", ["imap", "offline_access"], token).ConfigureAwait(false);
                Assert.IsNotNull(value);
                payload = JsonSerializer.SerializeToUtf8Bytes(new OAuthRefreshTokenRequest(value.RefreshToken, "fixture-client"), JsonOptions);
            }
            var now = DateTimeOffset.UtcNow;
            return new(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "oauth",
                code ? ApplicationOperations.OAuthAuthorizationCodeRedeem : ApplicationOperations.OAuthTokenRefresh,
                "application/json", payload, new Dictionary<string, string>(StringComparer.Ordinal), now, now.AddSeconds(60));
        }

        internal async Task<ApplicationResponse> InvokeAsync(ApplicationRequest request, CancellationToken token)
        {
            var scope = Services.CreateAsyncScope();
            await using var scopeLifetime = scope.ConfigureAwait(false);
            return await scope.ServiceProvider.GetRequiredService<IApplicationRequestDispatcher>().DispatchAsync(request, token).ConfigureAwait(false);
        }

        internal async Task<AuthenticatedMailUser?> AuthenticateAsync(string accessToken, CancellationToken token)
        {
            var scope = Services.CreateAsyncScope();
            await using var scopeLifetime = scope.ConfigureAwait(false);
            return await scope.ServiceProvider.GetRequiredService<IOAuthTokenService>().AuthenticateAccessTokenAsync(accessToken, "imap", token).ConfigureAwait(false);
        }

        internal async Task AssertEncryptedReceiptAsync(Guid operation, OAuthTokenValue value, CancellationToken token)
        {
            var database = Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            var row = await database.ApplicationOperationReceipts.SingleAsync(candidate => candidate.OperationId == operation, token).ConfigureAwait(false);
            var reference = new LargeObjectReference(row.ObjectProvider, row.ObjectName, row.PayloadLength, row.ObjectSha256, row.ObjectEntityTag);
            var destination = new MemoryStream();
            await using var destinationLifetime = destination.ConfigureAwait(false);
            await objects.CopyToAsync(reference, destination, token).ConfigureAwait(false);
            var envelope = Encoding.UTF8.GetString(destination.ToArray());
            Assert.IsFalse(envelope.Contains(value.AccessToken, StringComparison.Ordinal));
            Assert.IsFalse(envelope.Contains(value.RefreshToken, StringComparison.Ordinal));
            Assert.AreEqual(userId, row.UserId);
        }

        internal async Task AssertReplayAfterCredentialRemovalAsync(ApplicationRequest request, bool code,
            byte[] expected, CancellationToken token)
        {
            var credential = code
                ? JsonSerializer.Deserialize<OAuthAuthorizationCodeRedeemRequest>(request.Payload, JsonOptions)?.Code
                : JsonSerializer.Deserialize<OAuthRefreshTokenRequest>(request.Payload, JsonOptions)?.RefreshToken;
            Assert.IsNotNull(credential);
            var id = Guid.ParseExact(credential.AsSpan("mk8_ac_".Length, 32), "N");
            var database = Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            var removed = code
                ? await database.OAuthAuthorizationCodes.Where(candidate => candidate.Id == id).ExecuteDeleteAsync(token).ConfigureAwait(false)
                : await database.OAuthTokens.Where(candidate => candidate.Id == id).ExecuteDeleteAsync(token).ConfigureAwait(false);
            Assert.AreEqual(1, removed);
            Assert.AreSequenceEqual(expected, (await InvokeAsync(request, token).ConfigureAwait(false)).Payload);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_services is not null) await _services.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try { if (_source is not null) await _source.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    _protector?.Dispose();
                    await _server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private sealed class CompletionFaultConsumer(PostgresApplicationBus bus, bool acknowledged) : IApplicationRequestConsumer
    {
        internal ApplicationResponse? DispatchedResponse { get; private set; }
        internal int CompletionCalls { get; private set; }
        internal int FailureCalls { get; private set; }

        public Task<ApplicationRequestLease> WaitForRequestAsync(string workerId, CancellationToken cancellationToken = default) =>
            bus.WaitForRequestAsync(workerId, cancellationToken);
        public Task<ApplicationRequestLease?> TryClaimAsync(string workerId, CancellationToken cancellationToken = default) =>
            bus.TryClaimAsync(workerId, cancellationToken);
        public Task<bool> RenewLeaseAsync(ApplicationRequestLease lease, CancellationToken cancellationToken = default) =>
            bus.RenewLeaseAsync(lease, cancellationToken);

        public async Task CompleteAsync(ApplicationRequestLease lease, ApplicationResponse response,
            CancellationToken cancellationToken = default)
        {
            CompletionCalls++;
            DispatchedResponse = response;
            if (acknowledged)
                await bus.CompleteAsync(lease, response, cancellationToken).ConfigureAwait(false);
            throw new IOException("Controlled failure before or after actual completion acknowledgment.");
        }

        public Task FailAsync(ApplicationRequestLease lease, string errorCode, string errorDetail,
            CancellationToken cancellationToken = default)
        {
            FailureCalls++;
            return bus.FailAsync(lease, errorCode, errorDetail, cancellationToken);
        }
    }
}
