using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;
using mk8.email.OAuth;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
[DoNotParallelize]
public sealed class OAuthEndpointTests
{
    private const string Username = "oauth.user@example.com";
    private const string Password = "primary-password-for-oauth";

    [TestMethod]
    [Timeout(15_000)]
    public async Task ThunderbirdAuthorizationCodeFlowRotatesAndRevokesTokens()
    {
        await using var fixture = await OAuthFixture.CreateAsync();
        using var metadataResponse = await fixture.Client.GetAsync(
            "/.well-known/oauth-authorization-server");
        Assert.AreEqual(HttpStatusCode.OK, metadataResponse.StatusCode);
        using (var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual(
                "https://email.mk8n.com/oauth/authorize",
                metadata.RootElement.GetProperty("authorization_endpoint").GetString());
            CollectionAssert.Contains(
                metadata.RootElement.GetProperty("code_challenge_methods_supported")
                    .EnumerateArray().Select(item => item.GetString()).ToArray(),
                "S256");
        }

        var verifier = new string('v', 64);
        var challenge = OAuthProtocolValues.CreatePkceChallenge(verifier);
        const string redirectUri = "http://127.0.0.1:49152/";
        const string state = "state-value-123456789";
        var authorizationValues = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "thunderbird",
            ["redirect_uri"] = redirectUri,
            ["scope"] = "offline_access imap smtp jmap dav",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["login_hint"] = Username,
        };
        var authorizationQuery = string.Join('&', authorizationValues.Select(value =>
            $"{WebUtility.UrlEncode(value.Key)}={WebUtility.UrlEncode(value.Value)}"));
        using var beginResponse = await fixture.Client.GetAsync(
            $"/oauth/authorize?{authorizationQuery}");
        Assert.AreEqual(HttpStatusCode.OK, beginResponse.StatusCode);
        StringAssert.Contains(
            await beginResponse.Content.ReadAsStringAsync(),
            "Connect Thunderbird to mk8.email");
        var csrfCookie = GetCookie(beginResponse, "__Host-mk8oauth");

        var completionValues = new Dictionary<string, string>(authorizationValues)
        {
            ["csrf"] = csrfCookie,
            ["username"] = Username,
            ["password"] = Password,
            ["device_name"] = "Thunderbird integration test",
        };
        using var completionRequest = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize")
        {
            Content = new FormUrlEncodedContent(completionValues),
        };
        completionRequest.Headers.TryAddWithoutValidation(
            "Cookie",
            $"__Host-mk8oauth={csrfCookie}");
        using var completionResponse = await fixture.Client.SendAsync(completionRequest);
        Assert.AreEqual(HttpStatusCode.Redirect, completionResponse.StatusCode);
        var redirect = completionResponse.Headers.Location;
        Assert.IsNotNull(redirect);
        Assert.AreEqual("127.0.0.1", redirect.Host);
        var redirectValues = ParseQuery(redirect.Query);
        Assert.AreEqual(state, redirectValues["state"]);
        var code = redirectValues["code"];

        var initialTokens = await ExchangeAsync(fixture.Client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "thunderbird",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        });
        Assert.IsNotNull(initialTokens.AccessToken);
        Assert.IsNotNull(initialTokens.RefreshToken);
        Assert.AreEqual("Bearer", initialTokens.TokenType);

        using (var scope = fixture.Services.CreateScope())
        {
            var tokenService = scope.ServiceProvider.GetRequiredService<IOAuthTokenService>();
            Assert.IsNotNull(await tokenService.AuthenticateAccessTokenAsync(
                initialTokens.AccessToken,
                "imap"));
            Assert.IsNull(await tokenService.AuthenticateAccessTokenAsync(
                initialTokens.AccessToken,
                "pop"));
        }

        using var replayResponse = await fixture.Client.PostAsync(
            "/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = "thunderbird",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }));
        Assert.AreEqual(HttpStatusCode.BadRequest, replayResponse.StatusCode);
        StringAssert.Contains(await replayResponse.Content.ReadAsStringAsync(), "invalid_grant");

        var refreshed = await ExchangeAsync(fixture.Client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "thunderbird",
            ["refresh_token"] = initialTokens.RefreshToken,
        });
        Assert.AreNotEqual(initialTokens.AccessToken, refreshed.AccessToken);
        Assert.AreNotEqual(initialTokens.RefreshToken, refreshed.RefreshToken);

        using var revokeResponse = await fixture.Client.PostAsync(
            "/oauth/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = "thunderbird",
                ["token"] = refreshed.AccessToken,
            }));
        Assert.AreEqual(HttpStatusCode.OK, revokeResponse.StatusCode);
        using (var scope = fixture.Services.CreateScope())
        {
            var tokenService = scope.ServiceProvider.GetRequiredService<IOAuthTokenService>();
            Assert.IsNull(await tokenService.AuthenticateAccessTokenAsync(
                refreshed.AccessToken,
                "imap"));
            Assert.IsNull(await tokenService.RefreshAsync(
                refreshed.RefreshToken,
                "thunderbird"));
        }
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task AuthorizationEndpointRejectsMissingPkceAndCsrf()
    {
        await using var fixture = await OAuthFixture.CreateAsync();
        using var unsafeRedirect = await fixture.Client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=thunderbird"
            + "&redirect_uri=https%3A%2F%2Fattacker.example%2F"
            + "&scope=offline_access%20imap&state=state-value-123456789"
            + "&code_challenge=missing&code_challenge_method=S256");
        Assert.AreEqual(HttpStatusCode.BadRequest, unsafeRedirect.StatusCode);

        var verifier = new string('v', 64);
        using var missingCsrf = await fixture.Client.PostAsync(
            "/oauth/authorize",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = "thunderbird",
                ["redirect_uri"] = "http://127.0.0.1:49152/",
                ["scope"] = "offline_access imap",
                ["state"] = "state-value-123456789",
                ["code_challenge"] = OAuthProtocolValues.CreatePkceChallenge(verifier),
                ["code_challenge_method"] = "S256",
                ["username"] = Username,
                ["password"] = Password,
                ["device_name"] = "Thunderbird",
            }));
        Assert.AreEqual(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
    }

    private static async Task<TokenResponse> ExchangeAsync(
        HttpClient client,
        IReadOnlyDictionary<string, string> values)
    {
        using var response = await client.PostAsync(
            "/oauth/token",
            new FormUrlEncodedContent(values));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new(
            json.RootElement.GetProperty("access_token").GetString()!,
            json.RootElement.GetProperty("refresh_token").GetString()!,
            json.RootElement.GetProperty("token_type").GetString()!);
    }

    private static string GetCookie(HttpResponseMessage response, string name)
    {
        var header = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(name + "=", StringComparison.Ordinal));
        return header[(name.Length + 1)..].Split(';', 2)[0];
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => WebUtility.UrlDecode(pair[0]),
                pair => WebUtility.UrlDecode(pair.Length == 2 ? pair[1] : string.Empty),
                StringComparer.Ordinal);

    private sealed record TokenResponse(
        string AccessToken,
        string RefreshToken,
        string TokenType);

    private sealed class OAuthFixture : IAsyncDisposable
    {
        private readonly WebApplication application;

        private OAuthFixture(WebApplication application, HttpClient client)
        {
            this.application = application;
            Client = client;
        }

        public HttpClient Client { get; }
        public IServiceProvider Services => application.Services;

        public static async Task<OAuthFixture> CreateAsync()
        {
            var environment = new EnvironmentConfig
            {
                Smtp = new SmtpConfig { Hostname = "email.mk8n.com" },
                OAuth = new OAuthConfig
                {
                    EnableOAuth = true,
                    PublicBaseUrl = "https://email.mk8n.com",
                    ClientId = "thunderbird",
                    AccessTokenMinutes = 10,
                    RefreshTokenDays = 90,
                    AuthorizationCodeMinutes = 5,
                },
            };
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing",
            });
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton(environment);
            var databaseRoot = new InMemoryDatabaseRoot();
            var databaseName = $"oauth-endpoint-{Guid.NewGuid():N}";
            builder.Services.AddDbContext<EmailDbContext>(options =>
                options.UseInMemoryDatabase(databaseName, databaseRoot)
                    .ConfigureWarnings(warnings =>
                        warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            builder.Services.AddApplication();
            builder.Services.AddScoped<IMailAuthenticator, MailAuthenticator>();
            builder.Services.AddOAuthProtocol();

            var application = builder.Build();
            application.UseRouting();
            application.UseRateLimiter();
            application.MapOAuthEndpoints();

            using (var scope = application.Services.CreateScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
                var company = new CompanyDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "OAuth Endpoint Test",
                    IsActive = true,
                };
                database.Addresses.Add(new AddressDB
                {
                    Id = Guid.CreateVersion7(),
                    Domain = "example.com",
                    Company = company,
                    IsActive = true,
                });
                database.Users.Add(new UserDB
                {
                    Id = Guid.CreateVersion7(),
                    Username = Username,
                    PasswordHash = PasswordHasher.Hash(Password),
                    Company = company,
                    IsActive = true,
                });
                await database.SaveChangesAsync();
            }
            using (var verificationScope = application.Services.CreateScope())
            {
                var authenticated = await verificationScope.ServiceProvider
                    .GetRequiredService<IMailAuthenticator>()
                    .AuthenticatePrimaryAsync(Username, Password);
                Assert.IsNotNull(
                    authenticated,
                    "The OAuth fixture account must authenticate from a fresh service scope.");
            }

            await application.StartAsync();
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.Single()
                ?? throw new InvalidOperationException("The OAuth test server did not publish an address.");
            var client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            })
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(10),
            };
            return new OAuthFixture(application, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
