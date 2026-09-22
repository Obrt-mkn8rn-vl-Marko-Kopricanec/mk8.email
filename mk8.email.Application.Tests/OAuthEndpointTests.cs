using System.Net;
using System.Security.Cryptography;
using System.Text;
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
using mk8.email.Configuration;
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
    public async Task OpenIdConnectCodeFlowSignsIdentityAndServesScopedUserInfo()
    {
        await using var fixture = await OAuthFixture.CreateAsync();
        using var discoveryResponse = await fixture.Client.GetAsync(
            "/.well-known/openid-configuration");
        Assert.AreEqual(HttpStatusCode.OK, discoveryResponse.StatusCode);
        using var discovery = JsonDocument.Parse(
            await discoveryResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(
            "https://email.mk8n.com/oauth/jwks",
            discovery.RootElement.GetProperty("jwks_uri").GetString());
        CollectionAssert.Contains(
            discovery.RootElement.GetProperty("scopes_supported")
                .EnumerateArray().Select(item => item.GetString()).ToArray(),
            "openid");

        using var jwksResponse = await fixture.Client.GetAsync("/oauth/jwks");
        Assert.AreEqual(HttpStatusCode.OK, jwksResponse.StatusCode);
        using var jwks = JsonDocument.Parse(await jwksResponse.Content.ReadAsStringAsync());
        var jwk = jwks.RootElement.GetProperty("keys")[0];
        Assert.AreEqual("RSA", jwk.GetProperty("kty").GetString());
        Assert.AreEqual("RS256", jwk.GetProperty("alg").GetString());

        using var missingBearer = await fixture.Client.GetAsync("/oauth/userinfo");
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingBearer.StatusCode);
        Assert.AreEqual(
            "Bearer error=\"invalid_token\"",
            missingBearer.Headers.WwwAuthenticate.Single().ToString());

        var verifier = new string('o', 64);
        const string redirectUri = "http://127.0.0.1:49153/";
        const string nonce = "openid-nonce-value-123456789";
        var authorizationValues = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "thunderbird",
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid profile email offline_access imap",
            ["state"] = "openid-state-value-123456789",
            ["nonce"] = nonce,
            ["max_age"] = "0",
            ["code_challenge"] = OAuthProtocolValues.CreatePkceChallenge(verifier),
            ["code_challenge_method"] = "S256",
            ["login_hint"] = Username,
        };
        var query = string.Join('&', authorizationValues.Select(value =>
            $"{WebUtility.UrlEncode(value.Key)}={WebUtility.UrlEncode(value.Value)}"));
        using var begin = await fixture.Client.GetAsync($"/oauth/authorize?{query}");
        Assert.AreEqual(HttpStatusCode.OK, begin.StatusCode);
        var csrf = GetCookie(begin, "__Host-mk8oauth");
        authorizationValues["csrf"] = csrf;
        authorizationValues["username"] = Username;
        authorizationValues["password"] = Password;
        authorizationValues["device_name"] = "Thunderbird OIDC test";
        using var completion = await SendAuthorizationAsync(
            fixture.Client,
            authorizationValues,
            csrf);
        Assert.AreEqual(HttpStatusCode.Redirect, completion.StatusCode);
        var code = ParseQuery(completion.Headers.Location!.Query)["code"];

        var tokens = await ExchangeAsync(fixture.Client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "thunderbird",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        });

        Assert.IsNotNull(tokens.IdToken);
        var claims = VerifyIdToken(tokens.IdToken, jwk);
        Assert.AreEqual("https://email.mk8n.com", claims.GetProperty("iss").GetString());
        Assert.AreEqual("thunderbird", claims.GetProperty("aud").GetString());
        Assert.AreEqual(nonce, claims.GetProperty("nonce").GetString());
        Assert.AreEqual(Username, claims.GetProperty("email").GetString());
        Assert.IsTrue(claims.GetProperty("email_verified").GetBoolean());
        Assert.AreEqual(Username, claims.GetProperty("preferred_username").GetString());
        Assert.AreEqual(AccessTokenHash(tokens.AccessToken), claims.GetProperty("at_hash").GetString());
        Assert.IsTrue(claims.GetProperty("exp").GetInt64() > claims.GetProperty("iat").GetInt64());
        var subject = claims.GetProperty("sub").GetString();

        using var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, "/oauth/userinfo");
        userInfoRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var userInfoResponse = await fixture.Client.SendAsync(userInfoRequest);
        Assert.AreEqual(HttpStatusCode.OK, userInfoResponse.StatusCode);
        using (var userInfo = JsonDocument.Parse(await userInfoResponse.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual(subject, userInfo.RootElement.GetProperty("sub").GetString());
            Assert.AreEqual(Username, userInfo.RootElement.GetProperty("email").GetString());
            Assert.AreEqual(Username, userInfo.RootElement.GetProperty("preferred_username").GetString());
        }

        var refreshed = await ExchangeAsync(fixture.Client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "thunderbird",
            ["refresh_token"] = tokens.RefreshToken,
        });
        Assert.IsNotNull(refreshed.IdToken);
        var refreshedClaims = VerifyIdToken(refreshed.IdToken, jwk);
        Assert.AreEqual(subject, refreshedClaims.GetProperty("sub").GetString());
        Assert.AreEqual(
            claims.GetProperty("auth_time").GetInt64(),
            refreshedClaims.GetProperty("auth_time").GetInt64());
        Assert.IsFalse(refreshedClaims.TryGetProperty("nonce", out _));
        Assert.AreEqual(
            AccessTokenHash(refreshed.AccessToken),
            refreshedClaims.GetProperty("at_hash").GetString());

        using (var scope = fixture.Services.CreateScope())
        {
            var expectedSubject = await scope.ServiceProvider
                .GetRequiredService<EmailDbContext>()
                .Users.Select(user => user.Id.ToString("D"))
                .SingleAsync();
            Assert.AreEqual(expectedSubject, subject);
        }

        using var promptNone = await fixture.Client.GetAsync(
            $"/oauth/authorize?{query}&prompt=none");
        Assert.AreEqual(HttpStatusCode.Redirect, promptNone.StatusCode);
        Assert.AreEqual(
            "login_required",
            ParseQuery(promptNone.Headers.Location!.Query)["error"]);
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

    [TestMethod]
    [Timeout(15_000)]
    public async Task AuthorizationEndpointRequiresEnrolledMfaAndAcceptsRecoveryCode()
    {
        await using var fixture = await OAuthFixture.CreateAsync();
        var recoveryCode = await fixture.EnrollMfaAsync();
        var verifier = new string('m', 64);
        var values = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "thunderbird",
            ["redirect_uri"] = "http://127.0.0.1:49152/",
            ["scope"] = "offline_access imap",
            ["state"] = "mfa-state-123456789",
            ["code_challenge"] = OAuthProtocolValues.CreatePkceChallenge(verifier),
            ["code_challenge_method"] = "S256",
            ["login_hint"] = Username,
        };
        var query = string.Join('&', values.Select(value =>
            $"{WebUtility.UrlEncode(value.Key)}={WebUtility.UrlEncode(value.Value)}"));
        using var begin = await fixture.Client.GetAsync($"/oauth/authorize?{query}");
        var csrf = GetCookie(begin, "__Host-mk8oauth");
        values["csrf"] = csrf;
        values["username"] = Username;
        values["password"] = Password;
        values["device_name"] = "Thunderbird MFA test";

        using var missingCode = await SendAuthorizationAsync(fixture.Client, values, csrf);
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingCode.StatusCode);

        values["mfa_code"] = recoveryCode;
        using var authorized = await SendAuthorizationAsync(fixture.Client, values, csrf);
        Assert.AreEqual(HttpStatusCode.Redirect, authorized.StatusCode);
        Assert.IsTrue(authorized.Headers.Location?.Query.Contains("code=", StringComparison.Ordinal));
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
            json.RootElement.GetProperty("token_type").GetString()!,
            json.RootElement.TryGetProperty("id_token", out var idToken)
                ? idToken.GetString()
                : null);
    }

    private static JsonElement VerifyIdToken(string token, JsonElement jwk)
    {
        var segments = token.Split('.');
        Assert.HasCount(3, segments);
        using var header = JsonDocument.Parse(Base64UrlDecode(segments[0]));
        Assert.AreEqual("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.AreEqual(jwk.GetProperty("kid").GetString(), header.RootElement.GetProperty("kid").GetString());
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Base64UrlDecode(jwk.GetProperty("n").GetString()!),
            Exponent = Base64UrlDecode(jwk.GetProperty("e").GetString()!),
        });
        Assert.IsTrue(rsa.VerifyData(
            Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"),
            Base64UrlDecode(segments[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
        using var payload = JsonDocument.Parse(Base64UrlDecode(segments[1]));
        return payload.RootElement.Clone();
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string AccessTokenHash(string accessToken)
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
        return Convert.ToBase64String(digest.AsSpan(0, digest.Length / 2))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string GetCookie(HttpResponseMessage response, string name)
    {
        var header = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(name + "=", StringComparison.Ordinal));
        return header[(name.Length + 1)..].Split(';', 2)[0];
    }

    private static Task<HttpResponseMessage> SendAuthorizationAsync(
        HttpClient client,
        IReadOnlyDictionary<string, string> values,
        string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize")
        {
            Content = new FormUrlEncodedContent(values),
        };
        request.Headers.TryAddWithoutValidation("Cookie", $"__Host-mk8oauth={csrf}");
        return client.SendAsync(request);
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
        string TokenType,
        string? IdToken);

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
            using var signingKey = RSA.Create(2048);
            var environment = new EnvironmentConfig
            {
                Smtp = new SmtpConfig { Hostname = "email.mk8n.com" },
                OAuth = new OAuthConfig
                {
                    EnableOAuth = true,
                    EnableOpenIdConnect = true,
                    PublicBaseUrl = "https://email.mk8n.com",
                    ClientId = "thunderbird",
                    AccessTokenMinutes = 10,
                    RefreshTokenDays = 90,
                    AuthorizationCodeMinutes = 5,
                    IdTokenMinutes = 10,
                    SigningKey = signingKey.ExportPkcs8PrivateKeyPem(),
                },
                Mfa = new MfaConfig
                {
                    EnableTotp = true,
                    Issuer = "mk8.email test",
                    EncryptionKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x5a, 32).ToArray()),
                    RecoveryCodeCount = 5,
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

        public async Task<string> EnrollMfaAsync()
        {
            using var scope = Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IMfaService>();
            var enrollment = await service.BeginTotpEnrollmentAsync(Username, "Test authenticator");
            Assert.IsTrue(enrollment.Succeeded);
            Assert.IsTrue(TotpMfa.TryDecodeSecret(enrollment.Secret!, out var secret));
            var confirmation = await service.ConfirmTotpEnrollmentAsync(
                Username,
                TotpMfa.ComputeCode(secret, DateTime.UtcNow));
            Assert.IsTrue(confirmation.Succeeded);
            return confirmation.RecoveryCodes![0];
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
