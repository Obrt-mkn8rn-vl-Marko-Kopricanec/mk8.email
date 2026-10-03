using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Dav;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Dav;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class HttpBearerAuthenticationTests
{
    [TestMethod]
    public async Task GatewayParsesJmapBearerAndForwardsDavBearerToWorker()
    {
        var environment = new EnvironmentConfig
        {
            OAuth = new OAuthConfig { EnableOAuth = true },
        };

        var jmapContext = CreateContext();
        Assert.IsTrue(GatewayJmapAuthentication.TryParse(
            jmapContext.Request,
            environment,
            out var jmapAuthentication));
        Assert.AreEqual(ProtocolAuthenticationKinds.BearerToken, jmapAuthentication.Kind, StringComparer.Ordinal);
        Assert.AreEqual("access-token", jmapAuthentication.Secret, StringComparer.Ordinal);

        var transport = new RecordingDavTransport();
        var davContext = CreateContext();
        var davUser = await GatewayDavHttpAuthentication.AuthenticateAsync(
            davContext,
            new GatewayDavStore(transport),
            environment,
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(davUser);
        Assert.AreEqual(ApplicationOperations.DavAuthenticate, transport.LastOperation, StringComparer.Ordinal);
        var forwarded = transport.LastAuthentication;
        Assert.IsNotNull(forwarded);
        Assert.AreEqual(ProtocolAuthenticationKinds.BearerToken, forwarded.Kind, StringComparer.Ordinal);
        Assert.AreEqual("access-token", forwarded.Secret, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task DavApplicationAuthenticatesBearerWithDavScope()
    {
        var fixture = (await DavFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var tokenService = new RecordingOAuthTokenService();
        var services = new ServiceCollection()
            .AddSingleton<IOAuthTokenService>(tokenService)
            .BuildServiceProvider();
        await using var servicesLifetime = services.ConfigureAwait(false);
        var application = new DavApplicationService(
            scope.ServiceProvider.GetRequiredService<DavStore>(),
            scope.ServiceProvider.GetRequiredService<DavSchedulingService>(),
            scope.ServiceProvider.GetRequiredService<IMailAuthenticator>(),
            services);

        var result = await application.AuthenticateAsync(
            new DavAuthenticationRequest(new ProtocolAuthentication(
                ProtocolAuthenticationKinds.BearerToken, null, "access-token")),
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(result.Value);
        Assert.AreEqual("dav", tokenService.LastScope, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ChallengesAdvertiseBearerAndPasswordFallbackWhenOAuthIsEnabled()
    {
        var environment = new EnvironmentConfig
        {
            OAuth = new OAuthConfig { EnableOAuth = true },
        };
        var jmapContext = new DefaultHttpContext();
        _ = GatewayJmapAuthentication.Unauthorized(jmapContext, environment);
        var jmapChallenges = jmapContext.Response.Headers.WWWAuthenticate.ToArray();
        CollectionAssert.Contains(jmapChallenges, "Bearer realm=\"mk8.email JMAP\"");
        CollectionAssert.Contains(
            jmapChallenges,
            "Basic realm=\"mk8.email JMAP\", charset=\"UTF-8\"");

        var davContext = new DefaultHttpContext();
        davContext.Response.Body = new MemoryStream();
        await GatewayDavHttpAuthentication.WriteUnauthorizedAsync(
            davContext,
            environment,
            CancellationToken.None).ConfigureAwait(false);
        var davChallenges = davContext.Response.Headers.WWWAuthenticate.ToArray();
        CollectionAssert.Contains(davChallenges, "Bearer realm=\"mk8.email DAV\"");
        CollectionAssert.Contains(
            davChallenges,
            "Basic realm=\"mk8.email DAV\", charset=\"UTF-8\"");
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer access-token";
        return context;
    }

    private sealed class RecordingDavTransport : IGatewayApplicationTransport
    {
        public string? LastOperation { get; private set; }
        public ProtocolAuthentication? LastAuthentication { get; private set; }

        public Task<TResponse> SendAsync<TRequest, TResponse>(
            string protocol,
            string operation,
            TRequest value,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("dav", protocol, StringComparer.Ordinal);
            LastOperation = operation;
            LastAuthentication = Assert.IsInstanceOfType<DavAuthenticationRequest>(value).Authentication;
            return Task.FromResult((TResponse)(object)new DavLookupResult<DavUser>(
                new DavUser(Guid.CreateVersion7(), "user@example.com")));
        }
    }

    private sealed class RecordingOAuthTokenService : IOAuthTokenService
    {
        public string? LastScope { get; private set; }

        public Task<AuthenticatedMailUser?> AuthenticateAccessTokenAsync(
            string accessToken,
            string requiredScope,
            CancellationToken cancellationToken = default)
        {
            LastScope = requiredScope;
            return Task.FromResult<AuthenticatedMailUser?>(
string.Equals(accessToken, "access-token"
, StringComparison.Ordinal) ? new(Guid.CreateVersion7(), "user@example.com")
                    : null);
        }

        public Task<OAuthTokenPair?> CreateGrantAsync(
            Guid userId,
            string clientId,
            string deviceName,
            IReadOnlyCollection<string> scopes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OAuthTokenPair?> RefreshAsync(
            string refreshToken,
            string clientId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<OAuthGrantSummary>> ListGrantsAsync(
            Guid userId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> RevokeGrantAsync(
            Guid userId,
            Guid grantId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RevokeTokenAsync(
            string token,
            string clientId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
