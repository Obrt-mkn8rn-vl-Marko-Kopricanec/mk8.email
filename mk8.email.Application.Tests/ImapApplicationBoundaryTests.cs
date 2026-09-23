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

        var password = await SendAsync<ImapPasswordAuthentication, ImapIdentityResult>(
            dispatcher,
            ApplicationOperations.ImapAuthenticatePassword,
            new ImapPasswordAuthentication("user@example.test", "secret"));
        Assert.AreEqual(application.UserId, password.UserId);
        Assert.AreEqual("user@example.test", password.Username);
        Assert.AreEqual("secret", application.LastPassword);

        var oauth = await SendAsync<ImapOAuthAuthentication, ImapIdentityResult>(
            dispatcher,
            ApplicationOperations.ImapAuthenticateOAuth,
            new ImapOAuthAuthentication("user@example.test", "access-token"));
        Assert.AreEqual(application.UserId, oauth.UserId);
        Assert.AreEqual("access-token", application.LastAccessToken);

        var mailboxes = await SendAsync<ImapMailboxListRequest, ImapMailboxListResult>(
            dispatcher,
            ApplicationOperations.ImapListMailboxes,
            new ImapMailboxListRequest(application.UserId, SubscribedOnly: true));
        Assert.HasCount(1, mailboxes.Mailboxes);
        Assert.AreEqual("INBOX", mailboxes.Mailboxes[0].FolderName);
        Assert.IsTrue(application.LastSubscribedOnly);

        var statuses = await SendAsync<ImapMailboxStatusRequest, ImapMailboxStatusResult>(
            dispatcher,
            ApplicationOperations.ImapGetMailboxStatuses,
            new ImapMailboxStatusRequest(
                application.UserId, ["INBOX"], true, true, true));
        Assert.AreEqual(2, statuses.Statuses["INBOX"].MessageCount);
        Assert.AreEqual(12L, statuses.Statuses["INBOX"].SizeBytes);

        var subscription = await SendAsync<ImapMailboxSubscriptionRequest, ImapMailboxSubscriptionResult>(
            dispatcher,
            ApplicationOperations.ImapSetMailboxSubscription,
            new ImapMailboxSubscriptionRequest(application.UserId, "INBOX", false));
        Assert.IsTrue(subscription.Found);
        Assert.IsFalse(application.LastSubscriptionState);
    }

    private static async Task<TResponse> SendAsync<TRequest, TResponse>(
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
        return JsonSerializer.Deserialize<TResponse>(response.Payload, JsonOptions)
            ?? throw new InvalidOperationException("The IMAP application response was empty.");
    }

    private sealed class RecordingImapApplication : IImapApplicationService
    {
        public Guid UserId { get; } = Guid.CreateVersion7();
        public string? LastPassword { get; private set; }
        public string? LastAccessToken { get; private set; }
        public bool LastSubscribedOnly { get; private set; }
        public bool LastSubscriptionState { get; private set; } = true;

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

        public Task<ImapMailboxListResult> ListMailboxesAsync(
            ImapMailboxListRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSubscribedOnly = request.SubscribedOnly;
            return Task.FromResult(new ImapMailboxListResult(
                [new ImapMailboxInfo("user", "example.test", "INBOX", true, true)]));
        }

        public Task<ImapMailboxStatusResult> GetMailboxStatusesAsync(
            ImapMailboxStatusRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapMailboxStatusResult(new Dictionary<string, ImapMailboxStatus>
            {
                ["INBOX"] = new(
                    Guid.CreateVersion7(), 1, 3, 5, "mailbox-id", 2, 1, 12),
            }));

        public Task<ImapMailboxSubscriptionResult> SetMailboxSubscriptionAsync(
            ImapMailboxSubscriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSubscriptionState = request.IsSubscribed;
            return Task.FromResult(new ImapMailboxSubscriptionResult(true));
        }
    }
}
