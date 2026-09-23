using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class ImapApplicationBoundaryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task ImapAuthenticationCrossesTransportNeutralJsonBoundary()
    {
        var application = new RecordingImapApplication();
        await using var services = new ServiceCollection()
            .AddSingleton<IImapApplicationService>(application)
            .BuildServiceProvider();
        var dispatcher = new ApplicationRequestDispatcher(services);

        var password = await SendAsync<ImapPasswordAuthentication>(
            dispatcher,
            ApplicationOperations.ImapAuthenticatePassword,
            new ImapPasswordAuthentication("user@example.test", "secret"));
        Assert.AreEqual(application.UserId, password.UserId);
        Assert.AreEqual("user@example.test", password.Username);
        Assert.AreEqual("secret", application.LastPassword);

        var oauth = await SendAsync<ImapOAuthAuthentication>(
            dispatcher,
            ApplicationOperations.ImapAuthenticateOAuth,
            new ImapOAuthAuthentication("user@example.test", "access-token"));
        Assert.AreEqual(application.UserId, oauth.UserId);
        Assert.AreEqual("access-token", application.LastAccessToken);
    }

    private static async Task<ImapIdentityResult> SendAsync<TRequest>(
        ApplicationRequestDispatcher dispatcher,
        string operation,
        TRequest value)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new ApplicationRequest(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            "imap",
            operation,
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            new Dictionary<string, string>(),
            now,
            now.AddMinutes(1));
        var response = await dispatcher.DispatchAsync(request);
        Assert.IsFalse(response.IsError, response.ErrorCode);
        return JsonSerializer.Deserialize<ImapIdentityResult>(response.Payload, JsonOptions)
            ?? throw new InvalidOperationException("The IMAP authentication response was empty.");
    }

    private sealed class RecordingImapApplication : IImapApplicationService
    {
        public Guid UserId { get; } = Guid.CreateVersion7();
        public string? LastPassword { get; private set; }
        public string? LastAccessToken { get; private set; }

        public Task<ImapIdentityResult> AuthenticatePasswordAsync(
            ImapPasswordAuthentication request,
            CancellationToken cancellationToken = default)
        {
            LastPassword = request.Password;
            return Task.FromResult(new ImapIdentityResult(UserId, request.Username));
        }

        public Task<ImapIdentityResult> AuthenticateOAuthAsync(
            ImapOAuthAuthentication request,
            CancellationToken cancellationToken = default)
        {
            LastAccessToken = request.AccessToken;
            return Task.FromResult(new ImapIdentityResult(UserId, request.Username));
        }
    }
}
