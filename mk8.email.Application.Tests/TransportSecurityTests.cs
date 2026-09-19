using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TransportSecurityTests
{
    private const string TestUsername = "user@mk8n.com";
    private const string TestPassword = "correct horse battery staple";

    private string _testDirectory = null!;
    private string _certificatePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"mk8email-transport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _certificatePath = TestCertificateFactory.Create(_testDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpClearTextRejectsAuthenticationAndLongCommands()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("220 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("EHLO client.example");
        var capability = await connection.ReadSmtpResponseAsync();
        StringAssert.Contains(capability, "250-STARTTLS");
        Assert.IsFalse(capability.Contains("AUTH", StringComparison.Ordinal));
        StringAssert.Contains(capability, "250-8BITMIME");

        await connection.WriteLineAsync("AUTH PLAIN AGZvbwBiYXI=");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("538 ", StringComparison.Ordinal));

        await connection.WriteLineAsync(new string('X', 5000));
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("500 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRefreshesConnectionTimeoutAfterClientActivity()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port, connectionTimeoutSeconds: 2);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("220 ", StringComparison.Ordinal));
        for (var index = 0; index < 5; index++)
        {
            await Task.Delay(550);
            await connection.WriteLineAsync("NOOP");
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpStartTlsAdvertisesAuthenticationOnlyAfterUpgrade()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        Assert.IsFalse((await connection.ReadSmtpResponseAsync()).Contains("AUTH", StringComparison.Ordinal));

        await connection.WriteLineAsync("STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("220 ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");

        await connection.WriteLineAsync("EHLO client.example");
        var capability = await connection.ReadSmtpResponseAsync();
        StringAssert.Contains(capability, "250-AUTH PLAIN LOGIN");
        Assert.IsFalse(capability.Contains("STARTTLS", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImplicitTlsPeerFailuresDoNotProduceWarningLogs()
    {
        var smtpPort = ReservePort();
        var imapPort = ReservePort();
        var environment = CreateEnvironment(
            smtpImplicitTlsPort: smtpPort,
            imapImplicitTlsPort: imapPort);
        var smtpLogger = new CapturingLogger<SmtpServerService>();
        var imapLogger = new CapturingLogger<ImapServerService>();
        await using var smtpServer = await ServerFixture.StartSmtpAsync(
            environment,
            smtpPort,
            smtpLogger);
        await using var imapServer = await ServerFixture.StartImapAsync(
            environment,
            imapPort,
            imapLogger);

        await TriggerTlsPeerFailureAsync(smtpPort, sendMalformedPayload: false);
        await TriggerTlsPeerFailureAsync(smtpPort, sendMalformedPayload: true);
        await TriggerTlsPeerFailureAsync(imapPort, sendMalformedPayload: false);
        await TriggerTlsPeerFailureAsync(imapPort, sendMalformedPayload: true);

        await WaitForAsync(
            () => CountTlsHandshakeDebugLogs(smtpLogger) >= 2,
            "SMTP did not record both TLS peer failures at debug level.");
        await WaitForAsync(
            () => CountTlsHandshakeDebugLogs(imapLogger) >= 2,
            "IMAP did not record both TLS peer failures at debug level.");

        Assert.IsFalse(
            smtpLogger.Entries.Any(entry => entry.Level >= LogLevel.Warning),
            "SMTP recorded a warning for a TLS peer failure.");
        Assert.IsFalse(
            imapLogger.Entries.Any(entry => entry.Level >= LogLevel.Warning),
            "IMAP recorded a warning for a TLS peer failure.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImplicitTlsCertificateLoadFailuresRemainWarnings()
    {
        var smtpPort = ReservePort();
        var imapPort = ReservePort();
        var missingCertificatePath = Path.Combine(_testDirectory, "missing.pfx");
        var environment = CreateEnvironment(
            smtpImplicitTlsPort: smtpPort,
            imapImplicitTlsPort: imapPort,
            certificatePath: missingCertificatePath);
        var smtpLogger = new CapturingLogger<SmtpServerService>();
        var imapLogger = new CapturingLogger<ImapServerService>();
        await using var smtpServer = await ServerFixture.StartSmtpAsync(
            environment,
            smtpPort,
            smtpLogger);
        await using var imapServer = await ServerFixture.StartImapAsync(
            environment,
            imapPort,
            imapLogger);

        await WaitForAsync(
            () => HasCertificateLoadWarning(smtpLogger),
            "SMTP did not record the certificate load failure as a warning.");
        await WaitForAsync(
            () => HasCertificateLoadWarning(imapLogger),
            "IMAP did not record the certificate load failure as a warning.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpSubmissionRequiresTlsBeforeMail()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(submissionPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
        await connection.WriteLineAsync("MAIL FROM:<sender@mk8n.com>");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("530 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsMessageWhenBufferedDataReachesItsLimit()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@mk8n.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));

        var dataLine = new string('a', 900);
        for (var index = 0; index < 80; index++)
            await connection.WriteLineAsync(dataLine);
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("552 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);

        await connection.WriteLineAsync("NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task SmtpBoundsConcurrentDataBuffers()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        var connections = new List<ProtocolConnection>();

        try
        {
            for (var index = 0; index < 5; index++)
            {
                var connection = await ProtocolConnection.ConnectAsync(port);
                connections.Add(connection);
                await connection.ReadLineAsync();
                await BeginInboundEnvelopeAsync(connection);
                await connection.WriteLineAsync("DATA");

                var response = await connection.ReadLineAsync();
                if (index < 4)
                    Assert.IsTrue(response.StartsWith("354 ", StringComparison.Ordinal));
                else
                    Assert.IsTrue(response.StartsWith("452 4.3.2", StringComparison.Ordinal));
            }

            await connections[0].WriteLineAsync(".");
            Assert.IsTrue((await connections[0].ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));

            await connections[4].WriteLineAsync("DATA");
            Assert.IsTrue((await connections[4].ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsLongDataAndPreservesEightBitData()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await BeginInboundMessageAsync(connection);
        await connection.WriteLineAsync(new string('a', 999));
        await connection.WriteLineAsync(".");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("554 5.6.0", StringComparison.Ordinal));

        await BeginInboundMessageAsync(connection);
        await connection.WriteLineAsync("Subject: café");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
        StringAssert.Contains(server.MailQueue.LastSubmission!.RawMessage, "café");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpQueuesBoundedMessageAndResetsTransaction()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("RCPT TO:<postmaster@mk8n.com>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("DATA");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("Subject: bounded");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("message body");
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
        Assert.IsNotNull(server.MailQueue.LastSubmission);
        StringAssert.StartsWith(server.MailQueue.LastSubmission.RawMessage, "Received: from client.example");
        Assert.AreEqual("sender@example.com", server.MailQueue.LastSubmission.EnvelopeSender);
        Assert.AreEqual("postmaster@mk8n.com", server.MailQueue.LastSubmission.Recipients.Single().Address);
        Assert.IsTrue(server.MailQueue.LastSubmission.Recipients.Single().IsLocal);

        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("503 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpReturnsTemporaryFailureWhenQueuePersistenceFails()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        server.MailQueue.ThrowOnEnqueue = true;
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await AuthenticateSmtpAsync(connection);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("DATA");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync($"From: {TestUsername}");
        await connection.WriteLineAsync("To: recipient@example.com");
        await connection.WriteLineAsync("Subject: signing failure");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("451 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);

        await connection.WriteLineAsync("NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpAcceptsOwnedSenderWithMatchingFromHeader()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await AuthenticateSmtpAsync(connection);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));
        await connection.WriteLineAsync($"From: Test User <{TestUsername}>");
        await connection.WriteLineAsync("To: recipient@example.com");
        await connection.WriteLineAsync("Subject: authorized sender");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsUnownedEnvelopeSender()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await AuthenticateSmtpAsync(connection);
        await connection.WriteLineAsync("MAIL FROM:<other@mk8n.com>");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("553 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("503 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsMismatchedFromHeaderBeforeSigning()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await AuthenticateSmtpAsync(connection);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("DATA");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("From: other@mk8n.com");
        await connection.WriteLineAsync("To: recipient@example.com");
        await connection.WriteLineAsync("Subject: rejected sender");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("550 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("503 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsAuthenticationDuringMailTransaction()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await UpgradeSmtpToTlsAsync(connection);
        await connection.WriteLineAsync("MAIL FROM:<other@mk8n.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
        await connection.WriteLineAsync($"AUTH PLAIN {credentials}");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("503 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RSET");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync($"AUTH PLAIN {credentials}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("235 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRechecksSenderOwnershipBeforeDelivery()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await AuthenticateSmtpAsync(connection);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await server.DisableOwnedAddressAsync();
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("DATA");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync($"From: {TestUsername}");
        await connection.WriteLineAsync("To: recipient@example.com");
        await connection.WriteLineAsync("Subject: disabled sender");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("550 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapClearTextDisablesLoginAndHasNoByteOrderMark()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("* OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a1 CAPABILITY");
        var capability = await connection.ReadLineAsync();
        StringAssert.Contains(capability, "IMAP4rev1 LITERAL+ IDLE NAMESPACE SPECIAL-USE UIDPLUS");
        StringAssert.Contains(capability, "LIST-EXTENDED LIST-STATUS");
        StringAssert.Contains(capability, "ID ENABLE MOVE UNSELECT QUOTA CONDSTORE QRESYNC ESEARCH");
        StringAssert.Contains(capability, "MULTIAPPEND STATUS=SIZE APPENDLIMIT=65536");
        StringAssert.Contains(capability, "LOGINDISABLED");
        StringAssert.Contains(capability, "STARTTLS");
        Assert.IsFalse(capability.Contains("AUTH=PLAIN", StringComparison.Ordinal));
        foreach (var unverifiedExtension in new[]
                 {
                     "COMPRESS=DEFLATE", "BINARY", "OBJECTID", "SORT", "THREAD=REFERENCES",
                 })
        {
            Assert.IsFalse(
                capability.Contains(unverifiedExtension, StringComparison.Ordinal),
                $"The server advertised the unverified {unverifiedExtension} extension.");
        }
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a2 LOGIN user password");
        StringAssert.Contains(await connection.ReadLineAsync(), "[PRIVACYREQUIRED]");

        await connection.WriteLineAsync("a3 LOGIN {4}");
        var literalLoginResponse = await connection.ReadLineAsync();
        Assert.IsTrue(literalLoginResponse.StartsWith("a3 NO", StringComparison.Ordinal));
        StringAssert.Contains(literalLoginResponse, "[PRIVACYREQUIRED]");
        await connection.WriteLineAsync("a4 NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 LOGIN {4+}");
        literalLoginResponse = await connection.ReadLineAsync();
        Assert.IsTrue(literalLoginResponse.StartsWith("* BYE", StringComparison.Ordinal));
        StringAssert.Contains(literalLoginResponse, "[PRIVACYREQUIRED]");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapRefreshesConnectionTimeoutAfterClientActivity()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port, connectionTimeoutSeconds: 2);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("* OK", StringComparison.Ordinal));
        for (var index = 0; index < 5; index++)
        {
            await Task.Delay(550);
            var tag = $"a{index}";
            await connection.WriteLineAsync($"{tag} NOOP");
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith($"{tag} OK", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapStartTlsAdvertisesAuthenticationAfterUpgrade()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");

        await connection.WriteLineAsync("a2 CAPABILITY");
        var capability = await connection.ReadLineAsync();
        StringAssert.Contains(capability, "AUTH=PLAIN");
        StringAssert.Contains(capability, "SASL-IR");
        Assert.IsFalse(capability.Contains("LOGINDISABLED", StringComparison.Ordinal));
        Assert.IsFalse(capability.Contains("STARTTLS", StringComparison.Ordinal));
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAuthenticatePlainSupportsInitialResponseAndRejectsProxyAuthorization()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);

        await using (var connection = await ProtocolConnection.ConnectAsync(port))
        {
            await connection.ReadLineAsync();
            await connection.WriteLineAsync("a1 STARTTLS");
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
            await connection.UpgradeToTlsAsync("email.mk8n.com");

            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
            await connection.WriteLineAsync($"a2 AUTHENTICATE PLAIN {credentials}");
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync("a3 NOOP");
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK", StringComparison.Ordinal));
        }

        await using var rejected = await ProtocolConnection.ConnectAsync(port);
        await rejected.ReadLineAsync();
        await rejected.WriteLineAsync("b1 STARTTLS");
        Assert.IsTrue((await rejected.ReadLineAsync()).StartsWith("b1 OK", StringComparison.Ordinal));
        await rejected.UpgradeToTlsAsync("email.mk8n.com");

        var proxyCredentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"other@mk8n.com\0{TestUsername}\0{TestPassword}"));
        await rejected.WriteLineAsync($"b2 AUTHENTICATE PLAIN {proxyCredentials}");
        Assert.IsTrue((await rejected.ReadLineAsync()).StartsWith("b2 NO", StringComparison.Ordinal));
        await rejected.WriteLineAsync($"b3 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await rejected.ReadLineAsync()).StartsWith("b3 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapEnableRequiresAuthenticatedState()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 ENABLE QRESYNC");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a2 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a3 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 ENABLE QRESYNC CONDSTORE UNKNOWN");
        Assert.AreEqual("* ENABLED QRESYNC CONDSTORE", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5 SELECT INBOX");
        await ReadUntilTaggedResponseAsync(connection, "a5");
        await connection.WriteLineAsync("a6 ENABLE QRESYNC");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 BAD", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapRejectsUnknownCompressionWithoutChangingTheTransport()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 COMPRESS GZIP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapReportsNamespaceIdentityAndQuotaForThunderbirdDiscovery()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedInboxMessagesForSearchAsync();
        await server.SetUserQuotaAsync(4096);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 ID (\"name\" \"Thunderbird\")");
        StringAssert.Contains(await connection.ReadLineAsync(), "\"name\" \"mk8.email\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 NAMESPACE");
        Assert.AreEqual("* NAMESPACE ((\"\" \"/\")) NIL NIL", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 GETQUOTAROOT INBOX");
        Assert.AreEqual("* QUOTAROOT \"INBOX\" \"\"", await connection.ReadLineAsync());
        Assert.AreEqual("* QUOTA \"\" (STORAGE 1 4)", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 GETQUOTA \"\"");
        Assert.AreEqual("* QUOTA \"\" (STORAGE 1 4)", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 GETQUOTA missing");
        StringAssert.Contains(await connection.ReadLineAsync(), "[NONEXISTENT]");
        await connection.WriteLineAsync("a8 GETQUOTAROOT missing");
        StringAssert.Contains(await connection.ReadLineAsync(), "[NONEXISTENT]");

        await server.SetUserQuotaAsync(0);
        await connection.WriteLineAsync("a9 GETQUOTA \"\"");
        Assert.AreEqual("* QUOTA \"\" ()", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a9 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAcceptsBoundedCommandLiterals()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedInboxMessagesForSearchAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");

        await connection.WriteRawAsync(
            $"a2 LOGIN {{{TestUsername.Length}+}}\r\n{TestUsername} {{{TestPassword.Length}+}}\r\n{TestPassword}\r\n");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX");
        await ReadUntilTaggedResponseAsync(connection, "a3");

        const string subject = "Third \"quoted\" subject";
        await connection.WriteLineAsync($"a4 SEARCH SUBJECT {{{subject.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(subject);
        await connection.WriteLineAsync(string.Empty);
        var responses = await ReadUntilTaggedResponseAsync(connection, "a4");
        Assert.IsTrue(responses.Contains("* SEARCH 3"));

        const string bodyNeedle = "first body\r\n";
        await connection.WriteRawAsync($"a5 SEARCH BODY {{{bodyNeedle.Length}+}}\r\n{bodyNeedle}\r\n");
        responses = await ReadUntilTaggedResponseAsync(connection, "a5");
        Assert.IsTrue(responses.Contains("* SEARCH 1"));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAcceptsLiteralAppendMailboxName()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        const string message = "From: user@mk8n.com\r\nTo: user@mk8n.com\r\nSubject: literal mailbox\r\n\r\nbody\r\n";
        await connection.WriteRawAsync($"a3 APPEND {{4+}}\r\nSent {{{message.Length}}}\r\n");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));
        Assert.AreEqual("literal mailbox", (await server.GetStoredEmailAsync(DefaultFolders.Sent)).Subject);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapRejectsOversizedCommandLiteralsWithoutUnboundedReads()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);

        await using (var synchronized = await ProtocolConnection.ConnectAsync(port))
        {
            await synchronized.ReadLineAsync();
            await synchronized.WriteLineAsync("a1 ID {16385}");
            Assert.IsTrue((await synchronized.ReadLineAsync()).StartsWith("a1 BAD", StringComparison.Ordinal));
            await synchronized.WriteLineAsync("a2 NOOP");
            Assert.IsTrue((await synchronized.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        }

        await using var nonSynchronizing = await ProtocolConnection.ConnectAsync(port);
        await nonSynchronizing.ReadLineAsync();
        await nonSynchronizing.WriteLineAsync("b1 ID {16385+}");
        Assert.IsTrue((await nonSynchronizing.ReadLineAsync()).StartsWith("* BYE", StringComparison.Ordinal));

        await using var longLine = await ProtocolConnection.ConnectAsync(port);
        await longLine.ReadLineAsync();
        await longLine.WriteLineAsync(new string('X', 16_385));
        Assert.IsTrue((await longLine.ReadLineAsync()).StartsWith("* BYE", StringComparison.Ordinal));

        await using var append = await ProtocolConnection.ConnectAsync(port);
        await append.ReadLineAsync();
        await append.WriteLineAsync("c1 STARTTLS");
        Assert.IsTrue((await append.ReadLineAsync()).StartsWith("c1 OK", StringComparison.Ordinal));
        await append.UpgradeToTlsAsync("email.mk8n.com");
        await append.WriteLineAsync($"c2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await append.ReadLineAsync()).StartsWith("c2 OK", StringComparison.Ordinal));
        const string message = "From: user@mk8n.com\r\nTo: user@mk8n.com\r\nSubject: long continuation\r\n\r\nbody\r\n";
        await append.WriteLineAsync($"c3 APPEND \"Sent\" {{{message.Length}}}");
        Assert.IsTrue((await append.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await append.WriteRawAsync(message);
        await append.WriteLineAsync(new string('X', 16_385));
        Assert.IsTrue((await append.ReadLineAsync()).StartsWith("* BYE", StringComparison.Ordinal));
        Assert.AreEqual(0, await server.CountStoredEmailsAsync());
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapPrimaryMailboxUsesStandardInboxName()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");

        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 LIST \"\" \"*\"");
        var listLines = new List<string>();
        string line;
        do
        {
            line = await connection.ReadLineAsync();
            listLines.Add(line);
        }
        while (!line.StartsWith("a3 ", StringComparison.Ordinal));

        Assert.IsTrue(listLines.Any(value => value.Contains("\"INBOX\"", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a4 SELECT INBOX");
        do
        {
            line = await connection.ReadLineAsync();
        }
        while (!line.StartsWith("a4 ", StringComparison.Ordinal));

        Assert.IsTrue(line.StartsWith("a4 OK [READ-WRITE]", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ImapManagesPrimaryMailboxHierarchyUsingThunderbirdNames()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 CREATE Projects");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 CREATE Projects/2026");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5 CREATE Projects/2026");
        StringAssert.Contains(await connection.ReadLineAsync(), "[ALREADYEXISTS]");

        await connection.WriteLineAsync("a6 LIST \"\" \"*\"");
        var listed = await ReadUntilTaggedResponseAsync(connection, "a6");
        Assert.IsTrue(listed.Any(line =>
            line.Contains("(\\HasChildren)", StringComparison.Ordinal)
            && line.EndsWith("\"Projects\"", StringComparison.Ordinal)));
        Assert.IsTrue(listed.Any(line =>
            line.Contains("(\\HasNoChildren)", StringComparison.Ordinal)
            && line.EndsWith("\"Projects/2026\"", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a7 UNSUBSCRIBE Projects");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a7 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a8 LSUB \"\" \"*\"");
        var subscribed = await ReadUntilTaggedResponseAsync(connection, "a8");
        Assert.IsTrue(subscribed.Any(line =>
            line.Contains("(\\Noselect \\HasChildren)", StringComparison.Ordinal)
            && line.EndsWith("\"Projects\"", StringComparison.Ordinal)));
        Assert.IsTrue(subscribed.Any(line => line.EndsWith("\"Projects/2026\"", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a9 SELECT Projects/2026");
        var selected = await ReadUntilTaggedResponseAsync(connection, "a9");
        Assert.IsTrue(selected[^1].StartsWith("a9 OK [READ-WRITE]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a10 STATUS Projects/2026 (MESSAGES UIDNEXT)");
        Assert.AreEqual("* STATUS \"Projects/2026\" (MESSAGES 0 UIDNEXT 1)", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a10 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 RENAME Projects Archive");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a11 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a12 SELECT Projects/2026");
        StringAssert.Contains(await connection.ReadLineAsync(), "Mailbox not found");
        await connection.WriteLineAsync("a13 SELECT Archive/2026");
        selected = await ReadUntilTaggedResponseAsync(connection, "a13");
        Assert.IsTrue(selected[^1].StartsWith("a13 OK [READ-WRITE]", StringComparison.Ordinal));

        await connection.WriteLineAsync("a14 DELETE Archive");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a14 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a15 LIST \"\" \"Archive*\"");
        listed = await ReadUntilTaggedResponseAsync(connection, "a15");
        Assert.IsTrue(listed.Any(line =>
            line.Contains("(\\Noselect \\HasChildren)", StringComparison.Ordinal)
            && line.EndsWith("\"Archive\"", StringComparison.Ordinal)));
        Assert.IsTrue(listed.Any(line => line.EndsWith("\"Archive/2026\"", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a16 DELETE Archive/2026");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a16 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a17 LIST \"\" \"Archive*\"");
        listed = await ReadUntilTaggedResponseAsync(connection, "a17");
        Assert.AreEqual(1, listed.Count);
        Assert.IsTrue(listed[0].StartsWith("a17 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ImapRoundTripsInternationalMailboxNamesForThunderbird()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 CREATE Projects");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 CREATE \"Projects/&ZeVnLIqe-\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5 CREATE \"Projects/&U,BTFw-\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 LIST \"\" \"Projects/*\"");
        var listed = await ReadUntilTaggedResponseAsync(connection, "a6");
        Assert.IsTrue(listed.Any(line =>
            line.EndsWith("\"Projects/&ZeVnLIqe-\"", StringComparison.Ordinal)));
        Assert.IsTrue(listed.Any(line =>
            line.EndsWith("\"Projects/&U,BTFw-\"", StringComparison.Ordinal)));

        const string message =
            "From: user@mk8n.com\r\n" +
            "To: user@mk8n.com\r\n" +
            "Subject: international mailbox\r\n" +
            "\r\n" +
            "body\r\n";
        await connection.WriteLineAsync($"a7 APPEND \"Projects/&ZeVnLIqe-\" {{{message.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a7 OK [APPENDUID", StringComparison.Ordinal));

        await connection.WriteLineAsync("a8 SELECT \"Projects/&ZeVnLIqe-\"");
        var selected = await ReadUntilTaggedResponseAsync(connection, "a8");
        Assert.IsTrue(selected[^1].StartsWith("a8 OK [READ-WRITE]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a9 COPY 1 \"Projects/&U,BTFw-\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a9 OK [COPYUID", StringComparison.Ordinal));

        await connection.WriteLineAsync("a10 STATUS \"Projects/&U,BTFw-\" (MESSAGES)");
        Assert.AreEqual(
            "* STATUS \"Projects/&U,BTFw-\" (MESSAGES 1)",
            await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a10 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 GETQUOTAROOT \"Projects/&ZeVnLIqe-\"");
        var quota = await ReadUntilTaggedResponseAsync(connection, "a11");
        Assert.AreEqual("* QUOTAROOT \"Projects/&ZeVnLIqe-\" \"\"", quota[0]);

        await connection.WriteLineAsync("a12 RENAME \"Projects/&U,BTFw-\" \"Projects/A&-B\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a12 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a13 SELECT \"Projects/A&-B\"");
        selected = await ReadUntilTaggedResponseAsync(connection, "a13");
        Assert.IsTrue(selected[^1].StartsWith("a13 OK [READ-WRITE]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a14 MOVE 1 \"Projects/&ZeVnLIqe-\"");
        var moved = await ReadUntilTaggedResponseAsync(connection, "a14");
        Assert.IsTrue(moved[^1].StartsWith("a14 OK [COPYUID", StringComparison.Ordinal));

        await connection.WriteLineAsync("a15 STATUS \"Projects/&ZeVnLIqe-\" (MESSAGES)");
        Assert.AreEqual(
            "* STATUS \"Projects/&ZeVnLIqe-\" (MESSAGES 2)",
            await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a15 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a16 DELETE \"Projects/A&-B\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a16 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a17 CREATE \"&AEE-\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a17 BAD", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ImapListExtendedCombinesSubscriptionsSpecialUseAndStatus()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 CREATE Projects");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 CREATE Projects/2026");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5 CREATE Projects/2027");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5d CREATE Drafts");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5d OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5s CREATE Spam");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5s OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a6 UNSUBSCRIBE Projects");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a7 UNSUBSCRIBE Projects/2026");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a7 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a8 LIST (SUBSCRIBED RECURSIVEMATCH) \"\" \"%\" " +
            "RETURN (SUBSCRIBED CHILDREN STATUS (MESSAGES UIDNEXT SIZE))");
        var recursive = await ReadUntilTaggedResponseAsync(connection, "a8");
        var projects = recursive.Single(line =>
            line.StartsWith("* LIST (", StringComparison.Ordinal)
            && line.Contains("\"Projects\" (CHILDINFO (\"SUBSCRIBED\"))", StringComparison.Ordinal));
        Assert.IsFalse(projects.Contains("\\Subscribed", StringComparison.Ordinal));
        Assert.IsFalse(recursive.Any(line =>
            line.StartsWith("* STATUS \"Projects\"", StringComparison.Ordinal)));

        await connection.WriteLineAsync(
            "a9 LIST (SUBSCRIBED) \"\" (\"Projects/*\" \"Sent\") " +
            "RETURN (SUBSCRIBED STATUS (MESSAGES SIZE))");
        var subscribed = await ReadUntilTaggedResponseAsync(connection, "a9");
        Assert.IsFalse(subscribed.Any(line => line.Contains("Projects/2026", StringComparison.Ordinal)));
        Assert.IsTrue(subscribed.Any(line =>
            line.StartsWith("* LIST (", StringComparison.Ordinal)
            && line.Contains("\\Subscribed", StringComparison.Ordinal)
            && line.EndsWith("\"Projects/2027\"", StringComparison.Ordinal)));
        Assert.IsTrue(subscribed.Any(line =>
            line.StartsWith("* STATUS \"Projects/2027\" (MESSAGES 0 SIZE 0)", StringComparison.Ordinal)));
        Assert.IsTrue(subscribed.Any(line =>
            line.StartsWith("* LIST (", StringComparison.Ordinal)
            && line.Contains("\\Sent", StringComparison.Ordinal)
            && line.Contains("\\Subscribed", StringComparison.Ordinal)
            && line.EndsWith("\"Sent\"", StringComparison.Ordinal)));
        Assert.IsTrue(subscribed.Any(line =>
            line.StartsWith("* STATUS \"Sent\" (MESSAGES 0 SIZE 0)", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a10 LIST (SPECIAL-USE) \"\" \"*\"");
        var specialUse = await ReadUntilTaggedResponseAsync(connection, "a10");
        Assert.AreEqual(4, specialUse.Count(line => line.StartsWith("* LIST", StringComparison.Ordinal)));
        Assert.IsTrue(specialUse.Any(line => line.Contains("\\Drafts", StringComparison.Ordinal)));
        Assert.IsTrue(specialUse.Any(line => line.Contains("\\Junk", StringComparison.Ordinal)));
        Assert.IsTrue(specialUse.Any(line => line.Contains("\\Sent", StringComparison.Ordinal)));
        Assert.IsTrue(specialUse.Any(line => line.Contains("\\Trash", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a11 LIST (UNKNOWN) \"\" \"*\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a11 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a12 LIST (RECURSIVEMATCH) \"\" \"*\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a12 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a13 LIST \"\" \"\"");
        Assert.AreEqual("* LIST (\\Noselect) \"/\" \"\"", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a13 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapSearchMatchesOnlyTheRequestedHeaderValue()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedInboxMessagesForSearchAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX");

        string response;
        do
        {
            response = await connection.ReadLineAsync();
        }
        while (!response.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 UID SEARCH HEADER X-Mk8-Test mixedmarker42");
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 UID SEARCH SUBJECT mixedcasesubject");
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 UID SEARCH HEADER X-Mk8-Test absent-marker");
        Assert.AreEqual("* SEARCH", (await connection.ReadLineAsync()).TrimEnd());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ImapSearchEvaluatesBooleanGroupsAndBoundedMessageSets()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedInboxMessagesForSearchAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX");

        string response;
        do
        {
            response = await connection.ReadLineAsync();
        }
        while (!response.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a4 UID SEARCH OR SUBJECT MixedCaseSubject SUBJECT \"Other subject\"");
        Assert.AreEqual("* SEARCH 1 2", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a5 UID SEARCH NOT (OR SUBJECT MixedCaseSubject SUBJECT \"Other subject\")");
        Assert.AreEqual("* SEARCH 3", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a6 UID SEARCH SUBJECT \"Third \\\"quoted\\\" subject\"");
        Assert.AreEqual("* SEARCH 3", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a7 UID SEARCH SUBJECT \"Other subject\" NOT FROM third-header@example.net");
        Assert.AreEqual("* SEARCH 2", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a7 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a8 SEARCH 2:*");
        Assert.AreEqual("* SEARCH 2 3", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a8 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a9 UID SEARCH UID 1:2147483647");
        Assert.AreEqual("* SEARCH 1 2 3", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a9 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a10 UID SEARCH RETURN (MIN MAX COUNT ALL) SUBJECT absent-marker");
        Assert.AreEqual("* ESEARCH (TAG \"a10\") UID COUNT 0", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a10 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 SEARCH RETURN (MIN MAX COUNT ALL) ALL");
        Assert.AreEqual(
            "* ESEARCH (TAG \"a11\") MIN 1 MAX 3 COUNT 3 ALL 1:3",
            await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a11 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ImapSearchUsesHeaderBodyInternalAndSentDateSemantics()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedInboxMessagesForSearchAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX");

        string response;
        do
        {
            response = await connection.ReadLineAsync();
        }
        while (!response.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 UID SEARCH HEADER X-Unrelated mixedmarker42");
        Assert.AreEqual("* SEARCH 2", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 UID SEARCH TEXT \"prefix mixedmarker42\"");
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 UID SEARCH BODY body-only-needle");
        Assert.AreEqual("* SEARCH 3", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 UID SEARCH FROM third-header@example.net");
        Assert.AreEqual("* SEARCH 3", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a7 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a8 UID SEARCH BCC hidden@example.net");
        Assert.AreEqual("* SEARCH 3", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a8 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a9 UID SEARCH SENTON 5-Feb-2026");
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a9 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a10 UID SEARCH ON 2-Jan-2026");
        Assert.AreEqual("* SEARCH 2", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a10 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 UID SEARCH NEW");
        Assert.AreEqual("* SEARCH", (await connection.ReadLineAsync()).TrimEnd());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a11 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a12 UID SEARCH RECENT");
        Assert.AreEqual("* SEARCH", (await connection.ReadLineAsync()).TrimEnd());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a12 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a13 UID SEARCH OLD");
        Assert.AreEqual("* SEARCH 1 2 3", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a13 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ImapSearchRejectsInvalidCriteriaAndKeepsTheConnectionUsable()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedInboxMessagesForSearchAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX");

        string response;
        do
        {
            response = await connection.ReadLineAsync();
        }
        while (!response.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 UID SEARCH CHARSET UTF-8 ALL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith(
            "a4 NO [BADCHARSET (US-ASCII)]",
            StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 UID SEARCH UNKNOWN-CRITERION");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 UID SEARCH OR SUBJECT one");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 UID SEARCH NOT (ALL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a7 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a8 UID SEARCH SINCE invalid-date");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a8 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a9 NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a9 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAppendPreservesLiteralOctetsAndLeadingBodyLines()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        const string message =
            "From: user@mk8n.com\r\n" +
            "To: user@mk8n.com\r\n" +
            "Subject: café\r\n" +
            "\r\n" +
            "\r\nbody é\r\n";
        await connection.WriteLineAsync($"a3 APPEND \"Sent\" {{{message.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));

        var stored = await server.GetStoredEmailAsync(DefaultFolders.Sent);
        Assert.AreEqual("café", stored.Subject);
        Assert.AreEqual("\r\nbody é\r\n", stored.Body);
        Assert.AreEqual(message.Length, stored.SizeBytes);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapMultiAppendReportsAppendLimitAndMailboxSize()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        const string firstMessage =
            "From: user@mk8n.com\r\n" +
            "To: user@mk8n.com\r\n" +
            "Subject: first\r\n" +
            "\r\n" +
            "first body\r\n";
        const string secondMessage =
            "From: user@mk8n.com\r\n" +
            "To: user@mk8n.com\r\n" +
            "Subject: second\r\n" +
            "\r\n" +
            "second body\r\n";

        await connection.WriteLineAsync($"a3 APPEND \"Sent\" {{{firstMessage.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(firstMessage);
        await connection.WriteLineAsync($" (\\Seen) {{{secondMessage.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(secondMessage);
        await connection.WriteLineAsync(string.Empty);
        var appendResponse = await connection.ReadLineAsync();
        Assert.IsTrue(appendResponse.StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));
        StringAssert.Contains(appendResponse, " 1:2]");

        await connection.WriteLineAsync("a4 STATUS \"Sent\" (MESSAGES UIDNEXT SIZE)");
        Assert.AreEqual(
            $"* STATUS \"Sent\" (MESSAGES 2 UIDNEXT 3 SIZE {firstMessage.Length + secondMessage.Length})",
            await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));

        var stored = await server.GetStoredEmailsAsync(DefaultFolders.Sent);
        Assert.AreEqual(2, stored.Count);
        Assert.IsFalse(stored[0].IsRead);
        Assert.IsTrue(stored[1].IsRead);

        await connection.WriteLineAsync($"a5 APPEND \"Sent\" {{{firstMessage.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(firstMessage);
        await connection.WriteLineAsync(" invalid continuation");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 BAD", StringComparison.Ordinal));
        Assert.AreEqual(2, await server.CountStoredEmailsAsync());

        await connection.WriteLineAsync("a6 APPEND \"Sent\" {0}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 NO", StringComparison.Ordinal));
        Assert.AreEqual(2, await server.CountStoredEmailsAsync());
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAppendChecksQuotaBeforeReadingTheLiteral()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        const string message =
            "From: user@mk8n.com\r\n" +
            "To: user@mk8n.com\r\n" +
            "Subject: quota\r\n" +
            "\r\n" +
            "body\r\n";
        await server.SetUserQuotaAsync(message.Length - 1);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync($"a3 APPEND \"Sent\" {{{message.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 NO [OVERQUOTA]", StringComparison.Ordinal));
        Assert.AreEqual(0, await server.CountStoredEmailsAsync());

        await server.SetUserQuotaAsync(message.Length);
        await connection.WriteLineAsync($"a4 APPEND \"Sent\" {{{message.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK [APPENDUID", StringComparison.Ordinal));
        Assert.AreEqual(1, await server.CountStoredEmailsAsync());
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ImapBoundsConcurrentMessageWrites()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedSentMessagesWithReverseDatesAsync();
        const string message =
            "From: user@mk8n.com\r\n" +
            "To: user@mk8n.com\r\n" +
            "Subject: bounded append\r\n\r\n" +
            "body\r\n";
        var connections = new List<ProtocolConnection>();

        try
        {
            for (var index = 0; index < 3; index++)
            {
                var connection = await ProtocolConnection.ConnectAsync(port);
                connections.Add(connection);
                await connection.ReadLineAsync();
                await connection.WriteLineAsync("a1 STARTTLS");
                Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
                await connection.UpgradeToTlsAsync("email.mk8n.com");
                await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
                Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
                await connection.WriteLineAsync("a3 SELECT Sent");

                string response;
                do
                {
                    response = await connection.ReadLineAsync();
                }
                while (!response.StartsWith("a3 ", StringComparison.Ordinal));

                Assert.IsTrue(response.StartsWith("a3 OK", StringComparison.Ordinal));
            }

            for (var index = 0; index < 2; index++)
            {
                await connections[index].WriteLineAsync($"a4 APPEND \"Sent\" {{{message.Length}}}");
                Assert.IsTrue((await connections[index].ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
            }

            await connections[2].WriteLineAsync("a4 COPY 1 Trash");
            Assert.IsTrue((await connections[2].ReadLineAsync()).StartsWith("a4 NO [UNAVAILABLE]", StringComparison.Ordinal));
            await connections[2].WriteLineAsync($"a5 APPEND \"Sent\" {{{message.Length}}}");
            Assert.IsTrue((await connections[2].ReadLineAsync()).StartsWith("a5 NO [UNAVAILABLE]", StringComparison.Ordinal));

            await connections[0].WriteRawAsync(message);
            await connections[0].WriteLineAsync(string.Empty);
            Assert.IsTrue((await connections[0].ReadLineAsync()).StartsWith("a4 OK [APPENDUID", StringComparison.Ordinal));

            var accepted = false;
            for (var attempt = 0; attempt < 10 && !accepted; attempt++)
            {
                await connections[2].WriteLineAsync($"a6{attempt} APPEND \"Sent\" {{{message.Length}}}");
                var response = await connections[2].ReadLineAsync();
                accepted = response.StartsWith("+ ", StringComparison.Ordinal);
                if (!accepted)
                {
                    Assert.IsTrue(response.Contains("NO [UNAVAILABLE]", StringComparison.Ordinal));
                    await Task.Delay(20);
                }
            }

            Assert.IsTrue(accepted);
        }
        finally
        {
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapSequenceNumbersFollowUidOrder()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedSentMessagesWithReverseDatesAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 SELECT Sent");
        string line;
        do
        {
            line = await connection.ReadLineAsync();
        }
        while (!line.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 FETCH 1:* (UID)");
        var responses = new List<string>();
        do
        {
            line = await connection.ReadLineAsync();
            responses.Add(line);
        }
        while (!line.StartsWith("a4 ", StringComparison.Ordinal));

        Assert.IsTrue(responses[0].StartsWith("* 1 FETCH (UID 1)", StringComparison.Ordinal));
        Assert.IsTrue(responses[1].StartsWith("* 2 FETCH (UID 2)", StringComparison.Ordinal));
        Assert.IsTrue(responses[^1].StartsWith("a4 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapFetchStreamsSelectedBodyAndPersistsSeenFlag()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedSentMessagesWithReverseDatesAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT Sent");

        string line;
        do
        {
            line = await connection.ReadLineAsync();
        }
        while (!line.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 UID FETCH 2 (UID BODY.PEEK[TEXT])");
        var peekResponse = new List<string>();
        do
        {
            line = await connection.ReadLineAsync();
            peekResponse.Add(line);
        }
        while (!line.StartsWith("a4 ", StringComparison.Ordinal));

        Assert.IsTrue(peekResponse[0].StartsWith("* 2 FETCH", StringComparison.Ordinal));
        Assert.IsTrue(peekResponse.Any(value => value.Contains("BODY[TEXT] {6}", StringComparison.Ordinal)));
        Assert.IsTrue(peekResponse.Any(value => value == "body"));
        Assert.IsFalse((await server.GetStoredEmailByUidAsync(2)).IsRead);

        await connection.WriteLineAsync("a5 UID FETCH 2 (UID BODY[TEXT] MODSEQ)");
        do
        {
            line = await connection.ReadLineAsync();
        }
        while (!line.StartsWith("a5 ", StringComparison.Ordinal));

        var stored = await server.GetStoredEmailByUidAsync(2);
        Assert.IsTrue(stored.IsRead);
        Assert.AreEqual(3, stored.ModSeq);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ImapFetchProjectsMultipartBodyStructureAndNestedSections()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);
        const string message =
            "From: sender@example.net\r\n" +
            $"To: {TestUsername}\r\n" +
            "Subject: multipart message\r\n" +
            "Message-ID: <multipart@example.net>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"mix\"\r\n\r\n" +
            "--mix\r\n" +
            "Content-Type: multipart/alternative; boundary=\"alt\"\r\n\r\n" +
            "--alt\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n\r\n" +
            "plain=20body\r\n" +
            "--alt\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n\r\n" +
            "<html><body>html=20body</body></html>\r\n" +
            "--alt--\r\n" +
            "--mix\r\n" +
            "Content-Type: application/octet-stream; name=\"test.txt\"\r\n" +
            "Content-Disposition: attachment; filename=\"test.txt\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\n" +
            "YXR0YWNobWVudA==\r\n" +
            "--mix--\r\n";

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync($"a3 APPEND Sent {{{message.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 SELECT Sent");
        await ReadUntilTaggedResponseAsync(connection, "a4");

        await connection.WriteLineAsync("a5 UID FETCH 1 (UID BODYSTRUCTURE)");
        var bodyStructure = await connection.ReadLineAsync();
        StringAssert.Contains(bodyStructure, "BODYSTRUCTURE");
        StringAssert.Contains(bodyStructure, "\"ALTERNATIVE\"");
        StringAssert.Contains(bodyStructure, "\"MIXED\"");
        StringAssert.Contains(bodyStructure, "\"TEXT\" \"HTML\"");
        StringAssert.Contains(bodyStructure, "\"APPLICATION\" \"OCTET-STREAM\"");
        StringAssert.Contains(bodyStructure, "\"BASE64\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 UID FETCH 1 (UID BODY.PEEK[1.2])");
        var htmlSection = string.Join('\n', await ReadUntilTaggedResponseAsync(connection, "a6"));
        StringAssert.Contains(htmlSection, "BODY[1.2]");
        StringAssert.Contains(htmlSection, "<html><body>html=20body</body></html>");
        Assert.IsFalse((await server.GetStoredEmailByUidAsync(1)).IsRead);

        await connection.WriteLineAsync("a7 UID FETCH 1 (UID BODY.PEEK[2.MIME])");
        var attachmentHeaders = string.Join('\n', await ReadUntilTaggedResponseAsync(connection, "a7"));
        StringAssert.Contains(attachmentHeaders, "BODY[2.MIME]");
        StringAssert.Contains(attachmentHeaders, "Content-Type: application/octet-stream");
        StringAssert.Contains(attachmentHeaders, "Content-Disposition: attachment");
        Assert.IsFalse((await server.GetStoredEmailByUidAsync(1)).IsRead);

        await connection.WriteLineAsync("a8 UID FETCH 1 (UID BODY[2])");
        var attachment = string.Join('\n', await ReadUntilTaggedResponseAsync(connection, "a8"));
        StringAssert.Contains(attachment, "BODY[2]");
        StringAssert.Contains(attachment, "YXR0YWNobWVudA==");
        Assert.IsTrue((await server.GetStoredEmailByUidAsync(1)).IsRead);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapMutationsUseUidSequenceOrderWithoutChangingMessageContent()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedSentMessagesWithReverseDatesAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT Sent");

        string line;
        do
        {
            line = await connection.ReadLineAsync();
        }
        while (!line.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 SEARCH HEADER X-Test-Uid 1");
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 STORE 1 +FLAGS.SILENT (\\Deleted)");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a6 EXPUNGE");
        Assert.AreEqual("* 1 EXPUNGE", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 MOVE 1 Trash");
        Assert.AreEqual("* 1 EXPUNGE", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a7 OK [COPYUID", StringComparison.Ordinal));

        var moved = await server.GetStoredEmailAsync(DefaultFolders.Trash);
        Assert.AreEqual("UID 2", moved.Subject);
        Assert.AreEqual("body\r\n", moved.Body);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapCopyChecksUserQuotaAndPreservesSelectedContent()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedSentMessagesWithReverseDatesAsync();
        await server.SetUserQuotaAsync(299);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT Sent");

        string response;
        do
        {
            response = await connection.ReadLineAsync();
        }
        while (!response.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 COPY 1 Trash");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 NO [OVERQUOTA]", StringComparison.Ordinal));
        Assert.AreEqual(2, await server.CountStoredEmailsAsync());

        await server.SetUserQuotaAsync(300);
        await connection.WriteLineAsync("a5 COPY 1 Trash");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK [COPYUID", StringComparison.Ordinal));
        Assert.AreEqual(3, await server.CountStoredEmailsAsync());

        await server.SetUserQuotaAsync(399);
        await connection.WriteLineAsync("a6 UID COPY 2 Trash");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 NO [OVERQUOTA]", StringComparison.Ordinal));
        Assert.AreEqual(3, await server.CountStoredEmailsAsync());

        await server.SetUserQuotaAsync(400);
        await connection.WriteLineAsync("a7 UID COPY 2 Trash");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a7 OK [COPYUID", StringComparison.Ordinal));

        var copies = await server.GetStoredEmailsAsync(DefaultFolders.Trash);
        CollectionAssert.AreEqual(new[] { "UID 1", "UID 2" }, copies.Select(email => email.Subject).ToArray());
        Assert.IsTrue(copies.All(email => email.Body == "body\r\n"));
        Assert.AreEqual(4, await server.CountStoredEmailsAsync());
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapMovePersistsQresyncTombstones()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedSentMessagesWithReverseDatesAsync();

        await using (var connection = await ProtocolConnection.ConnectAsync(port))
        {
            await connection.ReadLineAsync();
            await connection.WriteLineAsync("a1 STARTTLS");
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
            await connection.UpgradeToTlsAsync("email.mk8n.com");
            await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync("a3 ENABLE QRESYNC");
            Assert.AreEqual("* ENABLED QRESYNC", await connection.ReadLineAsync());
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync("a4 SELECT Sent");
            await ReadUntilTaggedResponseAsync(connection, "a4");

            await connection.WriteLineAsync("a5 MOVE 1 Trash");
            Assert.AreEqual("* VANISHED 1", await connection.ReadLineAsync());
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK [COPYUID", StringComparison.Ordinal));

            await connection.WriteLineAsync("a6 UID MOVE 2 Trash");
            Assert.AreEqual("* VANISHED 2", await connection.ReadLineAsync());
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK [COPYUID", StringComparison.Ordinal));
        }

        await using var reconnect = await ProtocolConnection.ConnectAsync(port);
        await reconnect.ReadLineAsync();
        await reconnect.WriteLineAsync("b1 STARTTLS");
        Assert.IsTrue((await reconnect.ReadLineAsync()).StartsWith("b1 OK", StringComparison.Ordinal));
        await reconnect.UpgradeToTlsAsync("email.mk8n.com");
        await reconnect.WriteLineAsync($"b2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await reconnect.ReadLineAsync()).StartsWith("b2 OK", StringComparison.Ordinal));
        await reconnect.WriteLineAsync("b3 ENABLE QRESYNC");
        Assert.AreEqual("* ENABLED QRESYNC", await reconnect.ReadLineAsync());
        Assert.IsTrue((await reconnect.ReadLineAsync()).StartsWith("b3 OK", StringComparison.Ordinal));
        await reconnect.WriteLineAsync("b4 SELECT Sent (QRESYNC (1 2 1:2))");
        var responses = await ReadUntilTaggedResponseAsync(reconnect, "b4");

        Assert.IsTrue(responses.Contains("* VANISHED (EARLIER) 1:2"));
        Assert.IsTrue(responses.Contains("* OK [HIGHESTMODSEQ 4]"));
        Assert.AreEqual(2, (await server.GetStoredEmailsAsync(DefaultFolders.Trash)).Count);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapRejectsMalformedMessageSetsWithoutDisconnecting()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedSentMessagesWithReverseDatesAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT Sent");
        await ReadUntilTaggedResponseAsync(connection, "a3");

        var malformedCommands = new[]
        {
            "a4 FETCH 1:x (UID)",
            "a5 STORE 1::2 +FLAGS.SILENT (\\Seen)",
            "a6 UID COPY +1 Trash",
            "a7 MOVE 0 Trash",
            "a8 UID EXPUNGE 1:",
        };
        foreach (var command in malformedCommands)
        {
            await connection.WriteLineAsync(command);
            var tag = command[..command.IndexOf(' ')];
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith($"{tag} BAD", StringComparison.Ordinal));
        }

        await connection.WriteLineAsync("a9 STORE 1:2147483647 +FLAGS.SILENT (\\Seen)");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a9 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a10 NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a10 OK", StringComparison.Ordinal));
        Assert.AreEqual(2, await server.CountStoredEmailsAsync());
        Assert.AreEqual(0, (await server.GetStoredEmailsAsync(DefaultFolders.Trash)).Count);
    }

    private static async Task<List<string>> ReadUntilTaggedResponseAsync(
        ProtocolConnection connection,
        string tag)
    {
        var responses = new List<string>();
        string response;
        do
        {
            response = await connection.ReadLineAsync();
            responses.Add(response);
        }
        while (!response.StartsWith($"{tag} ", StringComparison.Ordinal));

        return responses;
    }

    private EnvironmentConfig CreateEnvironment(
        int? smtpPort = null,
        int? submissionPort = null,
        int? imapPort = null,
        int? smtpImplicitTlsPort = null,
        int? imapImplicitTlsPort = null,
        string? certificatePath = null,
        int connectionTimeoutSeconds = 10)
    {
        return new EnvironmentConfig
        {
            Smtp = new SmtpConfig
            {
                Hostname = "email.mk8n.com",
                Port = smtpPort ?? ReservePort(),
                SubmissionPort = submissionPort ?? ReservePort(),
                ImplicitTlsPort = smtpImplicitTlsPort ?? ReservePort(),
                EnableSmtp = smtpPort.HasValue,
                EnableSubmission = submissionPort.HasValue,
                EnableImplicitTls = smtpImplicitTlsPort.HasValue,
                EnableStartTls = true,
                RequireAuth = true,
                AllowRelay = true,
            },
            Imap = new ImapConfig
            {
                Port = imapPort ?? ReservePort(),
                ImplicitTlsPort = imapImplicitTlsPort ?? ReservePort(),
                EnableImap = imapPort.HasValue,
                EnableImplicitTls = imapImplicitTlsPort.HasValue,
            },
            Tls = new TlsConfig
            {
                CertificatePath = certificatePath ?? _certificatePath,
            },
            Limits = new LimitsConfig
            {
                MaxMessageSizeBytes = 65_536,
                MaxRecipientsPerMessage = 100,
                ConnectionTimeoutSeconds = connectionTimeoutSeconds,
                MaxConnectionsPerIp = 10,
            },
        };
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task TriggerTlsPeerFailureAsync(int port, bool sendMalformedPayload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        if (sendMalformedPayload)
        {
            var payload = "GET / HTTP/1.0\r\n\r\n"u8.ToArray();
            await client.GetStream().WriteAsync(payload, timeout.Token);
        }
    }

    private static int CountTlsHandshakeDebugLogs<T>(CapturingLogger<T> logger) =>
        logger.Entries.Count(entry =>
            entry.Level == LogLevel.Debug &&
            entry.Message.Contains("TLS handshake", StringComparison.Ordinal));

    private static bool HasCertificateLoadWarning<T>(CapturingLogger<T> logger) =>
        logger.Entries.Any(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception is not null &&
            entry.Message.Contains("Error handling", StringComparison.Ordinal));

    private static async Task WaitForAsync(Func<bool> condition, string failureMessage)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(20);

        Assert.IsTrue(condition(), failureMessage);
    }

    private static async Task AuthenticateSmtpAsync(ProtocolConnection connection)
    {
        await UpgradeSmtpToTlsAsync(connection);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
        await connection.WriteLineAsync($"AUTH PLAIN {credentials}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("235 ", StringComparison.Ordinal));
    }

    private static async Task BeginInboundMessageAsync(ProtocolConnection connection)
    {
        await BeginInboundEnvelopeAsync(connection);
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));
    }

    private static async Task BeginInboundEnvelopeAsync(ProtocolConnection connection)
    {
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@mk8n.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
    }

    private static async Task UpgradeSmtpToTlsAsync(ProtocolConnection connection)
    {
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
        await connection.WriteLineAsync("STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("220 ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
    }

    private sealed class ServerFixture(
        ServiceProvider services,
        IHostedService hostedService,
        StubEmailService emailService,
        StubMailSubmissionQueue mailQueue) : IAsyncDisposable
    {
        public StubEmailService EmailService { get; } = emailService;
        public StubMailSubmissionQueue MailQueue { get; } = mailQueue;

        public static async Task<ServerFixture> StartSmtpAsync(
            EnvironmentConfig environment,
            int port,
            ILogger<SmtpServerService>? logger = null)
        {
            var (services, emailService, mailQueue) = CreateServices();
            var hostedService = new SmtpServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                logger ?? NullLogger<SmtpServerService>.Instance);
            var fixture = new ServerFixture(services, hostedService, emailService, mailQueue);
            await fixture.StartAsync(port);
            return fixture;
        }

        public static async Task<ServerFixture> StartImapAsync(
            EnvironmentConfig environment,
            int port,
            ILogger<ImapServerService>? logger = null)
        {
            var (services, emailService, mailQueue) = CreateServices();
            var hostedService = new ImapServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                logger ?? NullLogger<ImapServerService>.Instance);
            var fixture = new ServerFixture(services, hostedService, emailService, mailQueue);
            await fixture.StartAsync(port);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await hostedService.StopAsync(timeout.Token);
            await services.DisposeAsync();
        }

        public async Task DisableOwnedAddressAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var address = await database.Addresses.SingleAsync(item => item.Domain == "mk8n.com");
            address.IsActive = false;
            await database.SaveChangesAsync();
        }

        public async Task SetUserQuotaAsync(long quotaBytes)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var user = await database.Users.SingleAsync(item => item.Username == TestUsername);
            user.QuotaBytes = quotaBytes;
            await database.SaveChangesAsync();
        }

        public async Task<int> CountStoredEmailsAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            return await database.Emails.CountAsync();
        }

        private static (
            ServiceProvider Services,
            StubEmailService EmailService,
            StubMailSubmissionQueue MailQueue) CreateServices()
        {
            var emailService = new StubEmailService();
            var mailQueue = new StubMailSubmissionQueue();
            var databaseName = $"transport-{Guid.NewGuid():N}";
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton<IEmailService>(emailService);
            serviceCollection.AddSingleton<IMailSubmissionQueue>(mailQueue);
            serviceCollection.AddScoped<ISenderAuthorizationService, SenderAuthorizationService>();
            serviceCollection.AddScoped<IMailAuthenticator, MailAuthenticator>();
            serviceCollection.AddDbContext<EmailDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
            var services = serviceCollection.BuildServiceProvider();
            using (var scope = services.CreateScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
                var company = new CompanyDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "Test Company",
                    IsActive = true,
                };
                var address = new AddressDB
                {
                    Id = Guid.CreateVersion7(),
                    Domain = "mk8n.com",
                    IsActive = true,
                    Company = company,
                };
                var user = new UserDB
                {
                    Id = Guid.CreateVersion7(),
                    Username = TestUsername,
                    PasswordHash = PasswordHasher.Hash(TestPassword),
                    Role = nameof(UserRole.User),
                    IsActive = true,
                    Company = company,
                };
                var inbox = new InboxDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "user",
                    Address = address,
                    Owner = user,
                };
                database.Inboxes.Add(inbox);
                database.Folders.Add(new FolderDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = DefaultFolders.Inbox,
                    Inbox = inbox,
                });
                database.Folders.Add(new FolderDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = DefaultFolders.Sent,
                    Inbox = inbox,
                });
                database.Folders.Add(new FolderDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = DefaultFolders.Trash,
                    Inbox = inbox,
                });
                database.SaveChanges();
            }
            return (services, emailService, mailQueue);
        }

        private async Task StartAsync(int port)
        {
            await hostedService.StartAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(20, timeout.Token);
                }
            }

            throw new TimeoutException($"The test server did not listen on port {port}.");
        }

        public async Task<EmailDB> GetStoredEmailAsync(string folderName)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            return await database.Emails
                .AsNoTracking()
                .Include(email => email.Folder)
                .SingleAsync(email => email.Folder.Name == folderName);
        }

        public async Task<List<EmailDB>> GetStoredEmailsAsync(string folderName)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            return await database.Emails
                .AsNoTracking()
                .Include(email => email.Folder)
                .Where(email => email.Folder.Name == folderName)
                .OrderBy(email => email.Uid)
                .ToListAsync();
        }

        public async Task<EmailDB> GetStoredEmailByUidAsync(int uid)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            return await database.Emails
                .AsNoTracking()
                .SingleAsync(email => email.Uid == uid);
        }

        public async Task SeedSentMessagesWithReverseDatesAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Sent);
            database.Emails.AddRange(
                CreateStoredEmail(folder.Id, uid: 1, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
                CreateStoredEmail(folder.Id, uid: 2, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            folder.NextUid = 3;
            folder.HighestModSeq = 2;
            await database.SaveChangesAsync();
        }

        public async Task SeedInboxMessagesForSearchAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Inbox);
            database.Emails.AddRange(
                new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Sender = "sender@example.net",
                    Recipient = TestUsername,
                    Subject = "MixedCaseSubject",
                    Body = "first body\r\n",
                    RawHeaders =
                        "From: first-header@example.net\r\n" +
                        $"To: {TestUsername}\r\n" +
                        "Date: Thu, 5 Feb 2026 23:30:00 +1400\r\n" +
                        "Subject: MixedCaseSubject\r\n" +
                        "X-Mk8-Test: prefix\r\n\tMiXeDMarker42",
                    SizeBytes = 180,
                    Uid = 1,
                    ModSeq = 1,
                    FolderId = folder.Id,
                    ReceivedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                },
                new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Sender = "sender@example.net",
                    Recipient = TestUsername,
                    Subject = "Other subject",
                    Body = "second body\r\n",
                    RawHeaders =
                        "From: second-header@example.net\r\n" +
                        $"To: {TestUsername}\r\n" +
                        "Date: Fri, 6 Feb 2026 00:15:00 +0000\r\n" +
                        "Subject: Other subject\r\n" +
                        "X-Mk8-Test: another value\r\n" +
                        "X-Unrelated: MiXeDMarker42",
                    SizeBytes = 190,
                    Uid = 2,
                    ModSeq = 2,
                    FolderId = folder.Id,
                    ReceivedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                },
                new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Sender = "envelope@example.net",
                    Recipient = TestUsername,
                    Subject = "Third \"quoted\" subject",
                    Body = "body-only-needle\r\n",
                    RawHeaders =
                        "From: third-header@example.net\r\n" +
                        $"To: {TestUsername}\r\n" +
                        "Bcc: hidden@example.net\r\n" +
                        "Date: Sat, 7 Feb 2026 12:00:00 -1000\r\n" +
                        "Subject: Third \"quoted\" subject\r\n" +
                        "X-Header-Only: header-only-needle",
                    SizeBytes = 200,
                    Uid = 3,
                    ModSeq = 3,
                    FolderId = folder.Id,
                    ReceivedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                });
            folder.NextUid = 4;
            await database.SaveChangesAsync();
        }

        private static EmailDB CreateStoredEmail(Guid folderId, int uid, DateTime receivedAt) => new()
        {
            Id = Guid.CreateVersion7(),
            Sender = "sender@example.net",
            Recipient = TestUsername,
            Subject = $"UID {uid}",
            Body = "body\r\n",
            RawHeaders = $"From: sender@example.net\r\nTo: {TestUsername}\r\nSubject: UID {uid}\r\nX-Test-Uid: {uid}",
            SizeBytes = 100,
            Uid = uid,
            ModSeq = uid,
            FolderId = folderId,
            ReceivedAt = receivedAt,
        };
    }

    private sealed class ProtocolConnection : IAsyncDisposable
    {
        private static readonly Encoding ProtocolEncoding = Encoding.Latin1;
        private readonly TcpClient _client;
        private Stream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;

        private ProtocolConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
            _reader = CreateReader(_stream);
            _writer = CreateWriter(_stream);
        }

        public static async Task<ProtocolConnection> ConnectAsync(int port)
        {
            var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return new ProtocolConnection(client);
        }

        public async Task<string> ReadLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await _reader.ReadLineAsync(timeout.Token)
                ?? throw new EndOfStreamException("The server closed the protocol stream.");
        }

        public async Task<string> ReadSmtpResponseAsync()
        {
            var response = new StringBuilder();
            while (true)
            {
                var line = await ReadLineAsync();
                if (response.Length > 0)
                    response.Append('\n');
                response.Append(line);

                if (line.Length >= 4 && line[3] != '-')
                    return response.ToString();
            }
        }

        public Task WriteLineAsync(string line) => _writer.WriteLineAsync(line);

        public async Task WriteRawAsync(string value)
        {
            await _writer.WriteAsync(value);
            await _writer.FlushAsync();
        }

        public async Task UpgradeToTlsAsync(string hostName)
        {
            await _writer.FlushAsync();
            _reader.Dispose();
            await _writer.DisposeAsync();

            var tlsStream = new SslStream(
                _stream,
                leaveInnerStreamOpen: false,
                (_, _, _, _) => true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tlsStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = hostName },
                timeout.Token);

            _stream = tlsStream;
            _reader = CreateReader(_stream);
            _writer = CreateWriter(_stream);
        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _writer.DisposeAsync();
            await _stream.DisposeAsync();
            _client.Dispose();
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

    private sealed record CapturedLog(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<CapturedLog> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new CapturedLog(logLevel, formatter(state, exception), exception));
    }

    private sealed class StubEmailService : IEmailService
    {
        public Task<bool> CanReceiveAsync(
            string recipient,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> DeliverAsync(
            string sender,
            string recipient,
            string rawMessage,
            string folderName = DefaultFolders.Inbox,
            Guid? queueDeliveryId = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The SMTP listener must use the durable queue.");

        public Task<bool> SaveSentCopyAsync(
            string sender,
            string rawMessage,
            Guid? queueDeliveryId = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The SMTP listener must use the durable queue.");
    }

    private sealed class StubMailSubmissionQueue : IMailSubmissionQueue
    {
        public bool ThrowOnEnqueue { get; set; }
        public int EnqueueCalls { get; private set; }
        public MailSubmission? LastSubmission { get; private set; }

        public Task<Guid> EnqueueAsync(
            MailSubmission submission,
            CancellationToken cancellationToken = default)
        {
            EnqueueCalls++;
            LastSubmission = submission;
            if (ThrowOnEnqueue)
                throw new IOException("Test queue failure.");
            return Task.FromResult(submission.QueueId);
        }
    }
}
