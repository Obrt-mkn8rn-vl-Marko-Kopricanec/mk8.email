using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Dav;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class HttpBearerAuthenticationTests
{
    [TestMethod]
    public async Task GatewayParsesJmapBearerAndDavUsesItsProtocolScope()
    {
        var tokenService = new RecordingOAuthTokenService();
        var services = new ServiceCollection()
            .AddSingleton<IOAuthTokenService>(tokenService)
            .BuildServiceProvider();
        var environment = new EnvironmentConfig
        {
            OAuth = new OAuthConfig { EnableOAuth = true },
        };
        var authenticator = new RejectingMailAuthenticator();

        var jmapContext = CreateContext(services);
        Assert.IsTrue(GatewayJmapAuthentication.TryParse(
            jmapContext.Request,
            environment,
            out var jmapAuthentication));
        Assert.AreEqual(ProtocolAuthenticationKinds.BearerToken, jmapAuthentication.Kind);
        Assert.AreEqual("access-token", jmapAuthentication.Secret);

        var davContext = CreateContext(services);
        var davUser = await DavHttpAuthentication.AuthenticateAsync(
            davContext,
            authenticator,
            environment,
            CancellationToken.None);
        Assert.IsNotNull(davUser);
        Assert.AreEqual("dav", tokenService.LastScope);
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
        await DavHttpAuthentication.WriteUnauthorizedAsync(
            davContext,
            environment,
            CancellationToken.None);
        var davChallenges = davContext.Response.Headers.WWWAuthenticate.ToArray();
        CollectionAssert.Contains(davChallenges, "Bearer realm=\"mk8.email DAV\"");
        CollectionAssert.Contains(
            davChallenges,
            "Basic realm=\"mk8.email DAV\", charset=\"UTF-8\"");
    }

    private static DefaultHttpContext CreateContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Authorization = "Bearer access-token";
        return context;
    }

    private sealed class RejectingMailAuthenticator : IMailAuthenticator
    {
        public Task<AuthenticatedMailUser?> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthenticatedMailUser?>(null);
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
                accessToken == "access-token"
                    ? new(Guid.CreateVersion7(), "user@example.com")
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
