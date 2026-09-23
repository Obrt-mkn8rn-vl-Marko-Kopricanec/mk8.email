using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class SmtpApplicationBoundaryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task GatewaySmtpOperationsCrossTypedApplicationBoundary()
    {
        var application = new RecordingApplication();
        await using var services = new ServiceCollection()
            .AddSingleton<ISmtpApplicationService>(application)
            .BuildServiceProvider();
        var gateway = new GatewaySmtpApplicationService(
            new InProcessTransport(new ApplicationRequestDispatcher(services)));

        Assert.AreEqual("user@example.test", (await gateway.AuthenticatePasswordAsync(
            new SmtpPasswordAuthentication("user@example.test", "secret"))).Username);
        Assert.AreEqual("user@example.test", (await gateway.AuthenticateOAuthAsync(
            new SmtpOAuthAuthentication("access-token"))).Username);
        Assert.IsTrue(await gateway.CanSendAsAsync(
            new SmtpSenderAuthorization("user@example.test", "user@example.test")));
        Assert.IsTrue(await gateway.HasMatchingFromAddressAsync(
            new SmtpFromAddressCheck("From: user@example.test\r\n\r\nbody", "user@example.test")));
        Assert.IsTrue(await gateway.CanReceiveAsync(
            new SmtpRecipientCheck("user@example.test")));

        var submission = new MailSubmission(
            Guid.CreateVersion7(),
            "user@example.test",
            [new MailEnvelopeRecipient("user@example.test", true)],
            "From: user@example.test\r\n\r\nbody",
            "127.0.0.1",
            "client.example.test",
            "user@example.test");
        Assert.AreEqual(submission.QueueId, await gateway.EnqueueAsync(submission));
        Assert.AreEqual("secret", application.Password?.Password);
        Assert.AreEqual("access-token", application.OAuth?.AccessToken);
        Assert.AreEqual(submission.RawMessage, application.Submission?.RawMessage);
        Assert.AreEqual(submission.Recipients[0], application.Submission?.Recipients[0]);
    }

    private sealed class InProcessTransport(ApplicationRequestDispatcher dispatcher)
        : IGatewayApplicationTransport
    {
        public async Task<TResponse> SendAsync<TRequest, TResponse>(
            string protocol,
            string operation,
            TRequest value,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("smtp", protocol);
            var now = DateTimeOffset.UtcNow;
            var request = new ApplicationRequest(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                0,
                protocol,
                operation,
                "application/json",
                JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
                new Dictionary<string, string>(),
                now,
                now.AddMinutes(1));
            var response = await dispatcher.DispatchAsync(request, cancellationToken);
            Assert.IsFalse(response.IsError, response.ErrorCode);
            return JsonSerializer.Deserialize<TResponse>(response.Payload, JsonOptions)
                ?? throw new InvalidOperationException("The application response was empty.");
        }
    }

    private sealed class RecordingApplication : ISmtpApplicationService
    {
        public SmtpPasswordAuthentication? Password { get; private set; }
        public SmtpOAuthAuthentication? OAuth { get; private set; }
        public MailSubmission? Submission { get; private set; }

        public Task<SmtpIdentityResult> AuthenticatePasswordAsync(
            SmtpPasswordAuthentication request,
            CancellationToken cancellationToken = default)
        {
            Password = request;
            return Task.FromResult(new SmtpIdentityResult(request.Username));
        }

        public Task<SmtpIdentityResult> AuthenticateOAuthAsync(
            SmtpOAuthAuthentication request,
            CancellationToken cancellationToken = default)
        {
            OAuth = request;
            return Task.FromResult(new SmtpIdentityResult("user@example.test"));
        }

        public Task<bool> CanSendAsAsync(
            SmtpSenderAuthorization request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(request.AuthenticatedUsername == request.SenderAddress);

        public Task<bool> HasMatchingFromAddressAsync(
            SmtpFromAddressCheck request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(request.RawMessage.Contains(
                $"From: {request.SenderAddress}", StringComparison.Ordinal));

        public Task<bool> CanReceiveAsync(
            SmtpRecipientCheck request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(request.Recipient == "user@example.test");

        public Task<Guid> EnqueueAsync(
            MailSubmission submission,
            CancellationToken cancellationToken = default)
        {
            Submission = submission;
            return Task.FromResult(submission.QueueId);
        }
    }
}
