using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class SmtpApplicationBoundaryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task GatewaySmtpOperationsCrossTypedApplicationBoundary()
    {
        var application = new RecordingApplication();
        var services = new ServiceCollection()
            .AddSingleton<ISmtpApplicationService>(application)
            .BuildServiceProvider();
        await using var servicesLifetime = services.ConfigureAwait(false);
        var gateway = new GatewaySmtpApplicationService(
            new InProcessTransport(new ApplicationRequestDispatcher(services)));

        Assert.AreEqual("user@example.test", (await gateway.AuthenticatePasswordAsync(
            new SmtpPasswordAuthentication("user@example.test", "secret")).ConfigureAwait(false)).Username, StringComparer.Ordinal);
        Assert.AreEqual("user@example.test", (await gateway.AuthenticateOAuthAsync(
            new SmtpOAuthAuthentication("access-token")).ConfigureAwait(false)).Username, StringComparer.Ordinal);
        Assert.IsTrue(await gateway.CanSendAsAsync(
            new SmtpSenderAuthorization("user@example.test", "user@example.test")).ConfigureAwait(false));
        Assert.IsTrue(await gateway.HasMatchingFromAddressAsync(
            new SmtpFromAddressCheck("From: user@example.test\r\n\r\nbody", "user@example.test")).ConfigureAwait(false));
        Assert.IsTrue(await gateway.CanReceiveAsync(
            new SmtpRecipientCheck("user@example.test")).ConfigureAwait(false));

        var submission = new MailSubmission(
            Guid.CreateVersion7(),
            "user@example.test",
            [new MailEnvelopeRecipient("user@example.test", true)],
            "From: user@example.test\r\n\r\nbody",
            "127.0.0.1",
            "client.example.test",
            "user@example.test");
        Assert.AreEqual(submission.QueueId, await gateway.EnqueueAsync(submission).ConfigureAwait(false));
        Assert.AreEqual("secret", application.Password?.Password, StringComparer.Ordinal);
        Assert.AreEqual("access-token", application.OAuth?.AccessToken, StringComparer.Ordinal);
        Assert.AreEqual(submission.RawMessage, application.Submission?.RawMessage, StringComparer.Ordinal);
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
            Assert.AreEqual("smtp", protocol, StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            var request = new ApplicationRequest(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                0,
                protocol,
                operation,
                "application/json",
                JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
                new Dictionary<string, string>(StringComparer.Ordinal),
                now,
                now.AddMinutes(1));
            var response = await dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
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
            Task.FromResult(string.Equals(request.AuthenticatedUsername, request.SenderAddress, StringComparison.Ordinal));

        public Task<bool> HasMatchingFromAddressAsync(
            SmtpFromAddressCheck request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(request.RawMessage.Contains(
                $"From: {request.SenderAddress}", StringComparison.Ordinal));

        public Task<bool> CanReceiveAsync(
            SmtpRecipientCheck request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(request.Recipient, "user@example.test", StringComparison.Ordinal));

        public Task<Guid> EnqueueAsync(
            MailSubmission submission,
            CancellationToken cancellationToken = default)
        {
            Submission = submission;
            return Task.FromResult(submission.QueueId);
        }
    }
}
