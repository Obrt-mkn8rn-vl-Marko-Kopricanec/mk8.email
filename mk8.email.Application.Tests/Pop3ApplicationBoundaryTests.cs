using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Pop3;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class Pop3ApplicationBoundaryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task Pop3OperationsCrossTransportNeutralJsonBoundary()
    {
        var application = new RecordingPop3Application();
        var services = new ServiceCollection()
            .AddSingleton<IPop3ApplicationService>(application)
            .BuildServiceProvider();
        await using var servicesLifetime = services.ConfigureAwait(false);
        var dispatcher = new ApplicationRequestDispatcher(services);
        var userId = Guid.CreateVersion7();
        var messageId = application.FoundMessageId;

        var password = await SendAsync<Pop3PasswordAuthentication, Pop3IdentityResult>(
            dispatcher,
            ApplicationOperations.Pop3AuthenticatePassword,
            new Pop3PasswordAuthentication("user@example.test", "secret")).ConfigureAwait(false);
        Assert.AreEqual("user@example.test", password.Username, StringComparer.Ordinal);
        var oauth = await SendAsync<Pop3OAuthAuthentication, Pop3IdentityResult>(
            dispatcher,
            ApplicationOperations.Pop3AuthenticateOAuth,
            new Pop3OAuthAuthentication("user@example.test", "access-token")).ConfigureAwait(false);
        Assert.AreEqual("access-token", application.LastAccessToken, StringComparer.Ordinal);
        Assert.IsNotNull(oauth.UserId);

        var snapshot = await SendAsync<Pop3UserRequest, Pop3MaildropSnapshot>(
            dispatcher,
            ApplicationOperations.Pop3ListMaildrop,
            new Pop3UserRequest(userId)).ConfigureAwait(false);
        Assert.HasCount(1, snapshot.Messages);
        Assert.AreEqual(userId, application.LastUserId);
        var message = await SendAsync<Pop3MessageRequest, Pop3MessageResult>(
            dispatcher,
            ApplicationOperations.Pop3GetMessage,
            new Pop3MessageRequest(userId, messageId)).ConfigureAwait(false);
        CollectionAssert.AreEqual("wire\r\n"u8.ToArray(), message.RawMessage);
        var missing = await SendAsync<Pop3MessageRequest, Pop3MessageResult>(
            dispatcher,
            ApplicationOperations.Pop3GetMessage,
            new Pop3MessageRequest(userId, Guid.CreateVersion7())).ConfigureAwait(false);
        Assert.IsNull(missing.RawMessage);

        var deleted = await SendAsync<Pop3DeleteRequest, Pop3DeleteResult>(
            dispatcher,
            ApplicationOperations.Pop3CommitDeletes,
            new Pop3DeleteRequest(userId, [messageId])).ConfigureAwait(false);
        Assert.AreEqual(1, deleted.DeletedCount);
        Assert.AreEqual(messageId, application.LastDeletedId);
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
            "pop3",
            operation,
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            new Dictionary<string, string>(StringComparer.Ordinal),
            now,
            now.AddMinutes(1));
        var response = await dispatcher.DispatchAsync(request).ConfigureAwait(false);
        Assert.IsFalse(response.IsError, response.ErrorCode);
        return JsonSerializer.Deserialize<TResponse>(response.Payload, JsonOptions)
            ?? throw new InvalidOperationException("The POP3 application response was empty.");
    }

    private sealed class RecordingPop3Application : IPop3ApplicationService
    {
        public Guid FoundMessageId { get; } = Guid.CreateVersion7();
        public string? LastAccessToken { get; private set; }
        public Guid LastUserId { get; private set; }
        public Guid LastDeletedId { get; private set; }

        public Task<Pop3IdentityResult> AuthenticatePasswordAsync(
            Pop3PasswordAuthentication request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Pop3IdentityResult(Guid.CreateVersion7(), request.Username));

        public Task<Pop3IdentityResult> AuthenticateOAuthAsync(
            Pop3OAuthAuthentication request,
            CancellationToken cancellationToken = default)
        {
            LastAccessToken = request.AccessToken;
            return Task.FromResult(new Pop3IdentityResult(Guid.CreateVersion7(), request.Username));
        }

        public Task<Pop3MaildropSnapshot> ListMaildropAsync(
            Pop3UserRequest request,
            CancellationToken cancellationToken = default)
        {
            LastUserId = request.UserId;
            return Task.FromResult(new Pop3MaildropSnapshot(
                [new Pop3MessageSummary(Guid.CreateVersion7(), 1, 6)]));
        }

        public Task<Pop3MessageResult> GetMessageAsync(
            Pop3MessageRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Pop3MessageResult(request.MessageId == FoundMessageId
                ? "wire\r\n"u8.ToArray()
                : null));

        public Task<Pop3DeleteResult> CommitDeletesAsync(
            Pop3DeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            LastDeletedId = request.MessageIds.Single();
            return Task.FromResult(new Pop3DeleteResult(1));
        }
    }
}
