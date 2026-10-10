using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DnsClient;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;
using mk8.email.Smtp.Presentation;

namespace mk8.email.Application.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class OutboundSmtpRelayTests
{
    private string _testDirectory = null!;
    private string _certificatePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"mk8email-relay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _certificatePath = TestCertificateFactory.Create(_testDirectory, "localhost");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }

    [TestMethod]
    public void MxResultsUsePreferenceAndImplicitFallback()
    {
        var explicitRoute = DnsMailExchangeResolver.CreateResult(
            "example.com",
            DnsHeaderResponseCode.NoError,
            [
                ("mx2.example.com.", (ushort)20),
                ("MX1.example.com.", (ushort)10),
                ("mx1.example.com.", (ushort)30),
            ]);

        Assert.AreEqual(MailRoutingStatus.Available, explicitRoute.Status);
        CollectionAssert.AreEqual(
            ExpectedVector1,
            explicitRoute.Exchanges.Select(exchange => exchange.Host).ToArray());
        CollectionAssert.AreEqual(
            new ushort[] { 10, 20 },
            explicitRoute.Exchanges.Select(exchange => exchange.Preference).ToArray());

        var implicitRoute = DnsMailExchangeResolver.CreateResult(
            "example.com",
            DnsHeaderResponseCode.NoError,
            []);

        Assert.AreEqual(MailRoutingStatus.Available, implicitRoute.Status);
        Assert.AreEqual("example.com", implicitRoute.Exchanges.Single().Host, StringComparer.Ordinal);
    }

    [TestMethod]
    public void NullMxAndNonexistentDomainStopRouting()
    {
        var nullMxRoute = DnsMailExchangeResolver.CreateResult(
            "example.com",
            DnsHeaderResponseCode.NoError,
            [(".", (ushort)0)]);
        var nonexistentRoute = DnsMailExchangeResolver.CreateResult(
            "example.com",
            DnsHeaderResponseCode.NotExistentDomain,
            []);

        Assert.AreEqual(MailRoutingStatus.DoesNotAcceptMail, nullMxRoute.Status);
        Assert.AreEqual(0, nullMxRoute.Exchanges.Count);
        Assert.AreEqual(MailRoutingStatus.DoesNotAcceptMail, nonexistentRoute.Status);
        Assert.AreEqual(0, nonexistentRoute.Exchanges.Count);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayUsesStartTlsAndDotStuffsMessage()
    {
        var server = new ScriptedSmtpServer(
            session => RunSuccessfulDeliveryAsync(session, useStartTls: true, _certificatePath));
        await using var serverLifetime = server.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@tenant.example.test",
            "recipient@example.com",
            "Subject: test\r\n\r\n.first\r\nlast\r\n").ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        Assert.IsTrue(server.Session!.UsedTls);
        CollectionAssert.Contains(server.Session.DataLines, "..first");
        Assert.AreEqual(1, server.ConnectionCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelaySupportsNullReversePathForAutomaticResponses()
    {
        var server = new ScriptedSmtpServer(
            session => RunSuccessfulDeliveryAsync(session, useStartTls: false, certificatePath: null));
        await using var serverLifetime = server.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            string.Empty,
            "recipient@example.com",
            "From: mailer-daemon@tenant.example.test\r\nSubject: response\r\n\r\nbody\r\n").ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        CollectionAssert.Contains(server.Session!.Commands, "MAIL FROM:<>");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayContinuesAfterTemporaryMxFailure()
    {
        var firstServer = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("421 4.3.0 Try another host").ConfigureAwait(false);
        });
        await using var firstServerLifetime = firstServer.ConfigureAwait(false);
        var secondServer = new ScriptedSmtpServer(
            session => RunSuccessfulDeliveryAsync(session, useStartTls: false, certificatePath: null));
        await using var secondServerLifetime = secondServer.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(new MailRoutingResult(
            MailRoutingStatus.Available,
            [
                new MailExchangeEndpoint("localhost", 10, firstServer.Port),
                new MailExchangeEndpoint("localhost", 20, secondServer.Port),
            ])));

        var result = await relay.RelayAsync(
            "sender@tenant.example.test",
            "recipient@example.com",
            "Subject: fallback\r\n\r\nbody").ConfigureAwait(false);
        await firstServer.WaitForCompletionAsync().ConfigureAwait(false);
        await secondServer.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        Assert.AreEqual(1, firstServer.ConnectionCount);
        Assert.AreEqual(1, secondServer.ConnectionCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayDoesNotDowngradeAfterStartTlsFailure()
    {
        var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250-receiver.test").ConfigureAwait(false);
            await session.WriteLineAsync("250 STARTTLS").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("220 Start TLS").ConfigureAwait(false);
        });
        await using var serverLifetime = server.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@tenant.example.test",
            "recipient@example.com",
            "Subject: no downgrade\r\n\r\nbody").ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.TemporaryFailure, result.Status);
        Assert.IsFalse(server.Session!.Commands.Any(command => command.StartsWith("MAIL ", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayStopsAfterPermanentRecipientFailure()
    {
        var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250 receiver.test").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250 Sender accepted").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("550 5.1.1 No such user").ConfigureAwait(false);
        });
        await using var serverLifetime = server.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@tenant.example.test",
            "recipient@example.com",
            "Subject: reject\r\n\r\nbody").ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.PermanentFailure, result.Status);
        Assert.AreEqual("5.1.1", result.EnhancedStatusCode, StringComparer.Ordinal);
        Assert.AreEqual("localhost", result.RemoteMta, StringComparer.Ordinal);
        Assert.IsFalse(server.Session!.Commands.Contains("DATA"));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayDoesNotSendEightBitContentWithoutRemoteCapability()
    {
        var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250 receiver.test").ConfigureAwait(false);
        });
        await using var serverLifetime = server.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@tenant.example.test",
            "recipient@example.com",
            "Subject: eight bit\r\n\r\ncafé").ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.PermanentFailure, result.Status);
        Assert.IsFalse(server.Session!.Commands.Any(command => command.StartsWith("MAIL ", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayDeclaresEightBitBodyWhenRemoteSupportsIt()
    {
        var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250-receiver.test").ConfigureAwait(false);
            await session.WriteLineAsync("250 8BITMIME").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250 Sender accepted").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250 Recipient accepted").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("354 Send message").ConfigureAwait(false);
            while (await session.ReadLineAsync().ConfigureAwait(false) is { } line && !string.Equals(line, ".", StringComparison.Ordinal))
                session.DataLines.Add(line);
            await session.WriteLineAsync("250 Queued").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
        });
        await using var serverLifetime = server.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@tenant.example.test",
            "recipient@example.com",
            "Subject: eight bit\r\n\r\ncafé").ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        CollectionAssert.Contains(
            server.Session!.Commands,
            "MAIL FROM:<sender@tenant.example.test> BODY=8BITMIME");
        CollectionAssert.Contains(server.Session.DataLines, "café");
    }

    [TestMethod]
    // The local socket exchanges have their own short deadlines; allow CI scheduling jitter.
    [Timeout(30_000)]
    public async Task RelayUsesSmtpUtf8ForInternationalizedEnvelopeAndHeaders()
    {
        var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250-receiver.test").ConfigureAwait(false);
            await session.WriteLineAsync("250-8BITMIME").ConfigureAwait(false);
            await session.WriteLineAsync("250 SMTPUTF8").ConfigureAwait(false);
            session.Commands.Add(await session.ReadUtf8LineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250 Sender accepted").ConfigureAwait(false);
            session.Commands.Add(await session.ReadUtf8LineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250 Recipient accepted").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("354 Send message").ConfigureAwait(false);
            while (await session.ReadUtf8LineAsync().ConfigureAwait(false) is { } line && !string.Equals(line, ".", StringComparison.Ordinal))
                session.DataLines.Add(line);
            await session.WriteLineAsync("250 Queued").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
        });
        await using var serverLifetime = server.ConfigureAwait(false);
        var resolver = new StubResolver(Available(server.Port));
        var relay = CreateRelay(resolver);
        const string message =
            "From: José <josé@tenant.example.test>\r\n" +
            "To: δοκιμή@bücher.example\r\n" +
            "Subject: Žuta pošta\r\n\r\n" +
            "Pozdrav\r\n";
        var wireMessage = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(message));

        var result = await relay.RelayAsync(
            "josé@tenant.example.test",
            "δοκιμή@bücher.example",
            wireMessage,
            new OutboundMailOptions(RequiresSmtpUtf8: true)).ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        Assert.AreEqual("xn--bcher-kva.example", resolver.LastDomain, StringComparer.Ordinal);
        CollectionAssert.Contains(
            server.Session!.Commands,
            "MAIL FROM:<josé@tenant.example.test> BODY=8BITMIME SMTPUTF8");
        CollectionAssert.Contains(
            server.Session.Commands,
            "RCPT TO:<δοκιμή@xn--bcher-kva.example>");
        CollectionAssert.Contains(server.Session.DataLines, "Subject: Žuta pošta");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayDoesNotSendInternationalizedMessageWithoutRemoteSmtpUtf8()
    {
        var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250-receiver.test").ConfigureAwait(false);
            await session.WriteLineAsync("250 8BITMIME").ConfigureAwait(false);
        });
        await using var serverLifetime = server.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(Available(server.Port)));
        var wireMessage = Encoding.Latin1.GetString(
            Encoding.UTF8.GetBytes("Subject: Žuta pošta\r\n\r\nbody\r\n"));

        var result = await relay.RelayAsync(
            "sender@tenant.example.test",
            "recipient@example.com",
            wireMessage,
            new OutboundMailOptions(RequiresSmtpUtf8: true)).ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.PermanentFailure, result.Status);
        Assert.IsFalse(
            server.Session!.Commands.Any(command => command.StartsWith("MAIL ", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayForwardsDeliveryStatusParametersWhenAdvertised()
    {
        var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250-receiver.test").ConfigureAwait(false);
            await session.WriteLineAsync("250 DSN").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250 2.1.0 Sender accepted").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("250 2.1.5 Recipient accepted").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("354 Send message").ConfigureAwait(false);
            while (await session.ReadLineAsync().ConfigureAwait(false) is { } line && !string.Equals(line, ".", StringComparison.Ordinal))
                session.DataLines.Add(line);
            await session.WriteLineAsync("250 2.0.0 Queued").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
        });
        await using var serverLifetime = server.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@tenant.example.test",
            "recipient@example.com",
            "Subject: DSN forwarding\r\n\r\nbody\r\n",
            new OutboundMailOptions(
                Dsn: new MailDsnEnvelope("hdrs", "job+2B42"),
                RecipientDsn: new MailDsnRecipient(
                    "success,failure",
                    "rfc822;old+2Btag+40example.com"))).ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        Assert.IsTrue(result.DsnParametersForwarded);
        Assert.AreEqual("2.0.0", result.EnhancedStatusCode, StringComparer.Ordinal);
        Assert.AreEqual("localhost", result.RemoteMta, StringComparer.Ordinal);
        CollectionAssert.Contains(
            server.Session!.Commands,
            "MAIL FROM:<sender@tenant.example.test> RET=HDRS ENVID=job+2B42");
        CollectionAssert.Contains(
            server.Session.Commands,
            "RCPT TO:<recipient@example.com> " +
            "NOTIFY=SUCCESS,FAILURE ORCPT=rfc822;old+2Btag+40example.com");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayOmitsDeliveryStatusParametersWhenNotAdvertised()
    {
        var server = new ScriptedSmtpServer(
            session => RunSuccessfulDeliveryAsync(
                session,
                useStartTls: false,
                certificatePath: null));
        await using var serverLifetime = server.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@tenant.example.test",
            "recipient@example.com",
            "Subject: DSN fallback\r\n\r\nbody\r\n",
            new OutboundMailOptions(
                Dsn: new MailDsnEnvelope("FULL", "job+2B42"),
                RecipientDsn: new MailDsnRecipient(
                    "SUCCESS,FAILURE",
                    "rfc822;old+2Btag+40example.com"))).ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        Assert.IsFalse(result.DsnParametersForwarded);
        CollectionAssert.Contains(server.Session!.Commands, "MAIL FROM:<sender@tenant.example.test>");
        CollectionAssert.Contains(server.Session.Commands, "RCPT TO:<recipient@example.com>");
        Assert.IsFalse(server.Session.Commands.Any(command =>
            command.Contains("RET=", StringComparison.Ordinal)
            || command.Contains("ENVID=", StringComparison.Ordinal)
            || command.Contains("NOTIFY=", StringComparison.Ordinal)
            || command.Contains("ORCPT=", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayUsesNullReversePathForNeverAgainstLegacyServer()
    {
        var server = new ScriptedSmtpServer(
            session => RunSuccessfulDeliveryAsync(
                session,
                useStartTls: false,
                certificatePath: null));
        await using var serverLifetime = server.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@tenant.example.test",
            "recipient@example.com",
            "Subject: no legacy bounce\r\n\r\nbody\r\n",
            new OutboundMailOptions(
                RecipientDsn: new MailDsnRecipient("NEVER"))).ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        Assert.IsFalse(result.DsnParametersForwarded);
        CollectionAssert.Contains(server.Session!.Commands, "MAIL FROM:<>");
        Assert.IsFalse(server.Session.Commands.Any(command =>
            command.Contains("NOTIFY=", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RelayRejectsCommandInjectionBeforeDnsLookup()
    {
        var resolver = new StubResolver(Available(25));
        var relay = CreateRelay(resolver);

        var result = await relay.RelayAsync(
            "sender@tenant.example.test\r\nRCPT TO:<attacker@example.com>",
            "recipient@example.com",
            "body").ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.PermanentFailure, result.Status);
        Assert.AreEqual(0, resolver.CallCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task GatewayRelayRecordsExactPlaintextAcrossStartTls()
    {
        var server = new ScriptedSmtpServer(
            session => RunSuccessfulDeliveryAsync(session, useStartTls: true, _certificatePath));
        await using var serverLifetime = server.ConfigureAwait(false);
        var journal = new RecordingJournal();
        var requestId = Guid.CreateVersion7();
        var relay = CreateRelay(new StubResolver(Available(server.Port)), journal);

        var result = await relay.RelayAsync(
            new SmtpRelayPresentationRequest(
                "sender@tenant.example.test",
                "recipient@example.com",
                "Subject: recorded\r\n\r\n.first\r\n",
                null),
            requestId,
            CancellationToken.None).ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        Assert.IsTrue(server.Session!.UsedTls);
        Assert.IsTrue(journal.Records.Count >= 8);
        Assert.IsTrue(journal.Records.All(record =>
            record.ApplicationRequestId == requestId
            && string.Equals(record.Protocol, SmtpPresentationOperations.Protocol
, StringComparison.Ordinal) && string.Equals(record.ContentType, "application/octet-stream", StringComparison.Ordinal)));
        Assert.AreEqual(1, journal.Records.Select(record => record.SessionId).Distinct().Count());
        CollectionAssert.AreEqual(
            Enumerable.Range(0, journal.Records.Count).Select(index => (long)index).ToArray(),
            journal.Records.Select(record => record.Sequence).ToArray());
        var outbound = Encoding.Latin1.GetString(journal.Records
            .Where(record => string.Equals(record.Direction, GatewayTrafficDirections.Outbound, StringComparison.Ordinal))
            .SelectMany(record => record.Payload)
            .ToArray());
        var inbound = Encoding.Latin1.GetString(journal.Records
            .Where(record => string.Equals(record.Direction, GatewayTrafficDirections.Inbound, StringComparison.Ordinal))
            .SelectMany(record => record.Payload)
            .ToArray());
        StringAssert.Contains(outbound, "EHLO email.tenant.example.test\r\n", StringComparison.Ordinal);
        StringAssert.Contains(outbound, "STARTTLS\r\n", StringComparison.Ordinal);
        StringAssert.Contains(outbound, "MAIL FROM:<sender@tenant.example.test>\r\n", StringComparison.Ordinal);
        StringAssert.Contains(outbound, "..first\r\n.\r\n", StringComparison.Ordinal);
        StringAssert.Contains(inbound, "220 receiver.test ESMTP\r\n", StringComparison.Ordinal);
        StringAssert.Contains(inbound, "250 Queued\r\n", StringComparison.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task GatewayRelayDoesNotSendCommandsWhenTrafficJournalFails()
    {
        var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP").ConfigureAwait(false);
            try
            {
                _ = await session.ReadLineAsync().ConfigureAwait(false);
                Assert.Fail("The relay sent a command despite an unavailable journal.");
            }
            catch (EndOfStreamException)
            {
            }
        });
        await using var serverLifetime = server.ConfigureAwait(false);
        var relay = CreateRelay(new StubResolver(Available(server.Port)), new RejectingJournal());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await relay.RelayAsync(
                new SmtpRelayPresentationRequest(
                    "sender@tenant.example.test",
                    "recipient@example.com",
                    "Subject: test\r\n\r\nbody\r\n",
                    null),
                Guid.CreateVersion7(),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await server.WaitForCompletionAsync().ConfigureAwait(false);
    }

    private static OutboundSmtpRelay CreateRelay(
        IMailExchangeResolver resolver,
        IGatewayTrafficJournal? traffic = null)
    {
        return new OutboundSmtpRelay(
            resolver,
            new EnvironmentConfig
            {
                Smtp = new SmtpConfig
                {
                    Hostname = "email.tenant.example.test",
                },
                Limits = new LimitsConfig
                {
                    ConnectionTimeoutSeconds = 10,
                },
            },
            NullLogger<OutboundSmtpRelay>.Instance,
            (_, certificate, _, errors) => TestCertificateFactory.IsTrusted(certificate, errors),
            traffic);
    }

    private sealed class RecordingJournal : IGatewayTrafficJournal
    {
        public List<GatewayTrafficRecord> Records { get; } = [];

        public Task AppendAsync(
            GatewayTrafficRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GatewayTrafficRecord>>(
                Records.Where(record => record.SessionId == sessionId).ToArray());
    }

    private sealed class RejectingJournal : IGatewayTrafficJournal
    {
        public Task AppendAsync(
            GatewayTrafficRecord record,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The traffic journal is unavailable.");

        public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The traffic journal is unavailable.");
    }

    private static MailRoutingResult Available(int port) => new(
        MailRoutingStatus.Available,
        [new MailExchangeEndpoint("localhost", 10, port)]);

    private static async Task RunSuccessfulDeliveryAsync(
        SmtpTestSession session,
        bool useStartTls,
        string? certificatePath)
    {
        await session.WriteLineAsync("220 receiver.test ESMTP").ConfigureAwait(false);
        session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));

        if (useStartTls)
        {
            await session.WriteLineAsync("250-receiver.test").ConfigureAwait(false);
            await session.WriteLineAsync("250 STARTTLS").ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
            await session.WriteLineAsync("220 Start TLS").ConfigureAwait(false);
            await session.UpgradeToTlsAsync(certificatePath!).ConfigureAwait(false);
            session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
        }

        await session.WriteLineAsync("250 receiver.test").ConfigureAwait(false);
        session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
        await session.WriteLineAsync("250 Sender accepted").ConfigureAwait(false);
        session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
        await session.WriteLineAsync("250 Recipient accepted").ConfigureAwait(false);
        session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
        await session.WriteLineAsync("354 Send message").ConfigureAwait(false);

        while (await session.ReadLineAsync().ConfigureAwait(false) is { } line && !string.Equals(line, ".", StringComparison.Ordinal))
            session.DataLines.Add(line);

        await session.WriteLineAsync("250 Queued").ConfigureAwait(false);
        session.Commands.Add(await session.ReadLineAsync().ConfigureAwait(false));
    }

    private sealed class StubResolver(MailRoutingResult result) : IMailExchangeResolver
    {
        public int CallCount { get; private set; }
        public string? LastDomain { get; private set; }

        public Task<MailRoutingResult> ResolveAsync(string domain, CancellationToken cancellationToken)
        {
            CallCount++;
            LastDomain = domain;
            return Task.FromResult(result);
        }
    }

    private sealed class ScriptedSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;

        public ScriptedSmtpServer(Func<SmtpTestSession, Task> script)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serverTask = RunAsync(script);
        }

        public int Port { get; }
        public int ConnectionCount { get; private set; }
        public SmtpTestSession? Session { get; private set; }

        public async Task WaitForCompletionAsync()
        {
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            _listener.Dispose();
            try
            {

                // This async test intentionally joins its pre-started background operation; no foreground synchronization context or JTF is involved.
#pragma warning disable VSTHRD003
                await _serverTask.ConfigureAwait(false);

#pragma warning restore VSTHRD003

            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
            }
            _cancellation.Dispose();
        }

        private async Task RunAsync(Func<SmtpTestSession, Task> script)
        {
            using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
            ConnectionCount++;
            var session = new SmtpTestSession(client.GetStream());
            await using var sessionLifetime = session.ConfigureAwait(false);
            Session = session;
            await script(session).ConfigureAwait(false);
        }
    }

    private sealed class SmtpTestSession(Stream initialStream) : IAsyncDisposable
    {
        private static readonly Encoding ProtocolEncoding = Encoding.Latin1;
        private Stream _stream = initialStream;
        private StreamReader _reader = CreateReader(initialStream);
        private StreamWriter _writer = CreateWriter(initialStream);

        public bool UsedTls { get; private set; }
        public List<string> Commands { get; } = [];
        public List<string> DataLines { get; } = [];

        public async Task<string> ReadLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The relay closed the test connection.");
        }

        public async Task<string> ReadUtf8LineAsync()
        {
            var wireValue = await ReadLineAsync().ConfigureAwait(false);
            return new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetString(Encoding.Latin1.GetBytes(wireValue));
        }

        public Task WriteLineAsync(string line) => _writer.WriteLineAsync(line);

        public async Task UpgradeToTlsAsync(string certificatePath)
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            _reader.Dispose();
            await _writer.DisposeAsync().ConfigureAwait(false);

            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password: null);
            var tlsStream = new SslStream(_stream, leaveInnerStreamOpen: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tlsStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions { ServerCertificate = certificate },
                timeout.Token).ConfigureAwait(false);

            _stream = tlsStream;
            _reader = CreateReader(_stream);
            _writer = CreateWriter(_stream);
            UsedTls = true;
        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _writer.DisposeAsync().ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        private static StreamReader CreateReader(Stream stream) =>
            new(stream, ProtocolEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        private static StreamWriter CreateWriter(Stream stream) =>
            new(stream, ProtocolEncoding, bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };
    }
    private static readonly string[] ExpectedVector1 = new[] { "MX1.example.com", "mx2.example.com" };
}
