using System.Collections.Concurrent;
using System.IO.Compression;
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
using mk8.email.Application.Protocol;
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
        StringAssert.Contains(capability, "250-SMTPUTF8");
        StringAssert.Contains(capability, "250-DSN");

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
    [Timeout(20_000)]
    public async Task ThunderbirdXOAuth2AuthenticatesSmtpImapAndPop3()
    {
        var smtpPort = ReservePort();
        var imapPort = ReservePort();
        var pop3Port = ReservePort();
        var environment = CreateEnvironment(
            smtpPort: smtpPort,
            imapPort: imapPort,
            pop3Port: pop3Port,
            enableOAuth: true);
        await using var smtpServer = await ServerFixture.StartSmtpAsync(environment, smtpPort);
        await using var imapServer = await ServerFixture.StartImapAsync(environment, imapPort);
        await using var pop3Server = await ServerFixture.StartPop3Async(environment, pop3Port);

        var smtpToken = await smtpServer.CreateOAuthAccessTokenAsync("smtp");
        await using (var smtp = await ProtocolConnection.ConnectAsync(smtpPort))
        {
            await smtp.ReadLineAsync();
            await smtp.WriteLineAsync("EHLO client.example");
            await smtp.ReadSmtpResponseAsync();
            await smtp.WriteLineAsync("STARTTLS");
            Assert.IsTrue((await smtp.ReadLineAsync()).StartsWith("220 ", StringComparison.Ordinal));
            await smtp.UpgradeToTlsAsync("email.mk8n.com");
            await smtp.WriteLineAsync("EHLO client.example");
            StringAssert.Contains(await smtp.ReadSmtpResponseAsync(), "XOAUTH2");
            await smtp.WriteLineAsync($"AUTH XOAUTH2 {CreateXOAuth2Response(smtpToken)}");
            Assert.IsTrue((await smtp.ReadLineAsync()).StartsWith("235 ", StringComparison.Ordinal));
        }

        var imapToken = await imapServer.CreateOAuthAccessTokenAsync("imap");
        await using (var imap = await ProtocolConnection.ConnectAsync(imapPort))
        {
            await imap.ReadLineAsync();
            await imap.WriteLineAsync("a1 STARTTLS");
            Assert.IsTrue((await imap.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
            await imap.UpgradeToTlsAsync("email.mk8n.com");
            await imap.WriteLineAsync("a2 CAPABILITY");
            StringAssert.Contains(await imap.ReadLineAsync(), "AUTH=XOAUTH2");
            Assert.IsTrue((await imap.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
            await imap.WriteLineAsync(
                $"a3 AUTHENTICATE XOAUTH2 {CreateXOAuth2Response(imapToken)}");
            Assert.IsTrue((await imap.ReadLineAsync()).StartsWith("a3 OK", StringComparison.Ordinal));
        }

        var pop3Token = await pop3Server.CreateOAuthAccessTokenAsync("pop");
        await using (var pop3 = await ProtocolConnection.ConnectAsync(pop3Port))
        {
            await pop3.ReadLineAsync();
            await pop3.WriteLineAsync("STLS");
            Assert.IsTrue((await pop3.ReadLineAsync()).StartsWith("+OK ", StringComparison.Ordinal));
            await pop3.UpgradeToTlsAsync("email.mk8n.com");
            await pop3.WriteLineAsync("CAPA");
            CollectionAssert.Contains(
                await ReadPop3MultilineAsync(pop3),
                "SASL PLAIN XOAUTH2");
            await pop3.WriteLineAsync("AUTH XOAUTH2");
            Assert.AreEqual("+ ", await pop3.ReadLineAsync());
            await pop3.WriteLineAsync(CreateXOAuth2Response(pop3Token));
            Assert.IsTrue((await pop3.ReadLineAsync()).StartsWith(
                "+OK maildrop has 0 messages",
                StringComparison.Ordinal));
        }
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

        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> BODY=8BITMIME");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@mk8n.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("Subject: eight-bit body");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body café");
        await connection.WriteLineAsync(".");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
        StringAssert.Contains(server.MailQueue.LastSubmission!.RawMessage, "café");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpAcceptsInternationalizedEnvelopeAndHeadersWithSmtpUtf8()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        StringAssert.Contains(await connection.ReadSmtpResponseAsync(), "250-SMTPUTF8");
        await connection.WriteUtf8LineAsync(
            "MAIL FROM:<josé@example.com> BODY=8BITMIME SMTPUTF8");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync(
            @"RCPT TO:<δοκιμή@mk8n.com> ORCPT=utf-8;\x{3B4}\x{3BF}\x{3BA}\x{3B9}\x{3BC}\x{3AE}@mk8n.com");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("From: José <josé@example.com>");
        await connection.WriteUtf8LineAsync("To: δοκιμή@mk8n.com");
        await connection.WriteUtf8LineAsync("Subject: Žuta pošta");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteUtf8LineAsync("Pozdrav iz Zagreba");
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        var submission = server.MailQueue.LastSubmission!;
        Assert.IsTrue(submission.RequiresSmtpUtf8);
        Assert.AreEqual("josé@example.com", submission.EnvelopeSender);
        var recipient = submission.Recipients.Single();
        Assert.AreEqual("δοκιμή@mk8n.com", recipient.Address);
        Assert.AreEqual(
            @"utf-8;\x{3B4}\x{3BF}\x{3BA}\x{3B9}\x{3BC}\x{3AE}@mk8n.com",
            recipient.Dsn!.OriginalRecipient);
        var decoded = Encoding.UTF8.GetString(
            Encoding.Latin1.GetBytes(submission.RawMessage));
        StringAssert.Contains(decoded, "with UTF8SMTP");
        StringAssert.Contains(decoded, "Subject: Žuta pošta");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpAcceptsAndQueuesDeliveryStatusParameters()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        StringAssert.Contains(await connection.ReadSmtpResponseAsync(), "250-DSN");
        await connection.WriteLineAsync(
            "MAIL FROM:<sender@example.com> RET=hdrs ENVID=job+2B42+3Ddone");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync(
            "RCPT TO:<postmaster@mk8n.com> " +
            "NOTIFY=success,failure,delay " +
            "ORCPT=rfc822;old+2Btag+40example.com");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("Subject: DSN request");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));

        var submission = server.MailQueue.LastSubmission!;
        Assert.AreEqual("HDRS", submission.Dsn!.ReturnContent);
        Assert.AreEqual("job+2B42+3Ddone", submission.Dsn.EnvelopeId);
        var recipient = submission.Recipients.Single();
        Assert.AreEqual("SUCCESS,FAILURE,DELAY", recipient.Dsn!.Notify);
        Assert.AreEqual("rfc822;old+2Btag+40example.com", recipient.Dsn.OriginalRecipient);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsMalformedOrMisplacedDeliveryStatusParameters()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("HELO client.example");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> RET=HDRS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("555 5.5.4", StringComparison.Ordinal));

        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
        await connection.WriteLineAsync(
            "MAIL FROM:<sender@example.com> RET=HDRS RET=FULL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> ENVID=job+2b42");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "RCPT TO:<postmaster@mk8n.com> NOTIFY=NEVER,FAILURE");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync(
            "RCPT TO:<postmaster@mk8n.com> " +
            "ORCPT=rfc822;one@example.com ORCPT=rfc822;two@example.com");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync(
            "RCPT TO:<postmaster@mk8n.com> ORCPT=utf-8;δοκιμή@example.com");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync(
            @"RCPT TO:<postmaster@mk8n.com> ORCPT=utf-8;\x{3B4}\x{3BF}\x{3BA}\x{3B9}\x{3BC}\x{3AE}@example.com");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@mk8n.com> FUTURE=value");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("555 5.5.4", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsUndeclaredInternationalizedContentAndInvalidParameters()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();

        await connection.WriteUtf8LineAsync("MAIL FROM:<josé@example.com> BODY=8BITMIME");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("550 5.6.7", StringComparison.Ordinal));

        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> BODY=8BITMIME");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("RCPT TO:<δοκιμή@mk8n.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("553 5.6.7", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@mk8n.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("Subject: Žuta pošta");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("554 5.6.9", StringComparison.Ordinal));

        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> FUTURE=value");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("555 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> SMTPUTF8=value");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> SIZE=999999999");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("552 5.3.4", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsInvalidUtf8CommandOctets()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteBytesAsync(
            Encoding.ASCII.GetBytes("EHLO client.")
                .Concat(new byte[] { 0xc3, 0x28 })
                .Concat("\r\n"u8.ToArray())
                .ToArray());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("500 5.5.2", StringComparison.Ordinal));

        await connection.WriteLineAsync("EHLO client.example");
        StringAssert.Contains(await connection.ReadSmtpResponseAsync(), "250-SMTPUTF8");
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
        StringAssert.Contains(capability, "SEARCHRES UTF8=ACCEPT");
        StringAssert.Contains(
            capability,
            "SORT THREAD=ORDEREDSUBJECT THREAD=REFERENCES MULTIAPPEND STATUS=SIZE " +
            "COMPRESS=DEFLATE APPENDLIMIT=65536");
        StringAssert.Contains(capability, "LOGINDISABLED");
        StringAssert.Contains(capability, "STARTTLS");
        Assert.IsFalse(capability.Contains("AUTH=PLAIN", StringComparison.Ordinal));
        foreach (var unverifiedExtension in new[]
                 {
                     "BINARY", "OBJECTID",
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

        await connection.WriteLineAsync("a4 ENABLE QRESYNC CONDSTORE UTF8=ACCEPT UNKNOWN");
        Assert.AreEqual("* ENABLED QRESYNC CONDSTORE UTF8=ACCEPT", await connection.ReadLineAsync());
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
    public async Task ImapCompressesCommandsAndResponsesAfterNegotiation()
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

        await connection.WriteLineAsync("a3 COMPRESS DEFLATE");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK", StringComparison.Ordinal));
        await connection.UpgradeToDeflateAsync();

        await connection.WriteLineAsync("a4 NOOP");
        Assert.AreEqual("a4 OK NOOP completed", await connection.ReadLineAsync());
        await connection.WriteLineAsync("a5 COMPRESS DEFLATE");
        Assert.AreEqual("a5 BAD COMPRESS already active", await connection.ReadLineAsync());
        await connection.WriteLineAsync("a6 LOGOUT");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("* BYE", StringComparison.Ordinal));
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task Pop3StartTlsSupportsThunderbirdRetrievalAndTransactionalDeletion()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(pop3Port: port);
        await using var server = await ServerFixture.StartPop3Async(environment, port);
        await server.SeedPop3MessagesAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("CAPA");
        var clearCapabilities = await ReadPop3MultilineAsync(connection);
        CollectionAssert.Contains(clearCapabilities, "STLS");
        CollectionAssert.Contains(clearCapabilities, "UIDL");
        CollectionAssert.Contains(clearCapabilities, "TOP");
        Assert.IsFalse(clearCapabilities.Any(line => line.StartsWith("SASL", StringComparison.Ordinal)));

        await connection.WriteLineAsync($"USER {TestUsername}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("-ERR [AUTH]", StringComparison.Ordinal));
        await connection.WriteLineAsync("STLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");

        await connection.WriteLineAsync("CAPA");
        var secureCapabilities = await ReadPop3MultilineAsync(connection);
        CollectionAssert.Contains(secureCapabilities, "SASL PLAIN");
        Assert.IsFalse(secureCapabilities.Contains("STLS"));

        await connection.WriteLineAsync($"USER {TestUsername}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync($"PASS {TestPassword}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+OK maildrop has 2 messages", StringComparison.Ordinal));

        await connection.WriteLineAsync("UIDL");
        var uidls = await ReadPop3MultilineAsync(connection);
        Assert.HasCount(3, uidls);
        Assert.IsTrue(uidls[1].StartsWith("1 E", StringComparison.Ordinal));
        Assert.IsTrue(uidls[2].StartsWith("2 E", StringComparison.Ordinal));
        Assert.AreNotEqual(uidls[1].Split(' ')[1], uidls[2].Split(' ')[1]);

        await connection.WriteLineAsync("TOP 1 1");
        var top = await ReadPop3MultilineAsync(connection);
        Assert.IsTrue(top[0].StartsWith("+OK ", StringComparison.Ordinal));
        CollectionAssert.Contains(top, "Subject: POP first");
        CollectionAssert.Contains(top, "..leading dot");
        Assert.IsFalse(top.Contains("second body line"));

        await connection.WriteLineAsync("RETR 1");
        var retrieved = await ReadPop3MultilineAsync(connection);
        Assert.IsTrue(retrieved[0].StartsWith("+OK ", StringComparison.Ordinal));
        CollectionAssert.Contains(retrieved, "..leading dot");
        CollectionAssert.Contains(retrieved, "second body line");

        await connection.WriteLineAsync("DELE 1");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("STAT");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+OK 1 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RSET");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+OK 2 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DELE 1");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("QUIT");
        Assert.AreEqual("+OK goodbye (1 messages deleted)", await connection.ReadLineAsync());

        Assert.AreEqual(1, await server.CountStoredEmailsAsync());
        Assert.AreEqual(1, await server.CountExpungedUidsAsync());
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task Pop3ImplicitTlsSupportsSaslPlainAndRollsBackAnAbandonedDelete()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(pop3ImplicitTlsPort: port);
        await using var server = await ServerFixture.StartPop3Async(environment, port);
        await server.SeedPop3MessagesAsync();

        await using (var connection = await ProtocolConnection.ConnectAsync(port))
        {
            await connection.UpgradeToTlsAsync("email.mk8n.com");
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+OK ", StringComparison.Ordinal));
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
            await connection.WriteLineAsync($"AUTH PLAIN {credentials}");
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+OK maildrop has 2 messages", StringComparison.Ordinal));
            await connection.WriteLineAsync("DELE 2");
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+OK ", StringComparison.Ordinal));
        }

        Assert.AreEqual(2, await server.CountStoredEmailsAsync());
        Assert.AreEqual(0, await server.CountExpungedUidsAsync());
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
    [Timeout(20_000)]
    public async Task ImapUtf8AcceptUsesDirectMailboxNamesSearchAndInternationalizedHeaders()
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

        await connection.WriteUtf8LineAsync("a3 CREATE \"Projects/Žuta pošta\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 BAD", StringComparison.Ordinal));

        const string internationalMessage =
            "From: user@mk8n.com\r\n" +
            "To: user@mk8n.com\r\n" +
            "Subject: Žuta pošta\r\n" +
            "\r\n" +
            "Pozdrav\r\n";
        var internationalMessageSize = Encoding.UTF8.GetByteCount(internationalMessage);
        await connection.WriteLineAsync($"a4 APPEND INBOX {{{internationalMessageSize}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteUtf8RawAsync(internationalMessage);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 NO [CANNOT]", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 ENABLE UTF8=ACCEPT");
        Assert.AreEqual("* ENABLED UTF8=ACCEPT", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 CREATE Projects");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("a7 CREATE \"Projects/Cafe\u0301\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a7 OK", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("a8 CREATE \"Projects/Žuta pošta\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a8 OK", StringComparison.Ordinal));

        var invalidUtf8Command = Encoding.ASCII.GetBytes("a9 CREATE \"Projects/")
            .Concat(new byte[] { 0xc3, 0x28 })
            .Concat(Encoding.ASCII.GetBytes("\"\r\n"))
            .ToArray();
        await connection.WriteBytesAsync(invalidUtf8Command);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a9 BAD", StringComparison.Ordinal));

        await connection.WriteUtf8LineAsync("a10 CREATE \"Projects/bad\u2028name\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a10 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 LIST \"\" \"Projects/*\"");
        var listed = new List<string>();
        string line;
        do
        {
            line = await connection.ReadUtf8LineAsync();
            listed.Add(line);
        }
        while (!line.StartsWith("a11 ", StringComparison.Ordinal));
        Assert.IsTrue(listed.Any(value => value.EndsWith("\"Projects/Café\"", StringComparison.Ordinal)));
        Assert.IsTrue(listed.Any(value => value.EndsWith("\"Projects/Žuta pošta\"", StringComparison.Ordinal)));

        await connection.WriteUtf8LineAsync("a12 STATUS \"Projects/Žuta pošta\" (MESSAGES)");
        Assert.AreEqual(
            "* STATUS \"Projects/Žuta pošta\" (MESSAGES 0)",
            await connection.ReadUtf8LineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a12 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync($"a13 APPEND INBOX {{{internationalMessageSize}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteUtf8RawAsync(internationalMessage);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a13 OK [APPENDUID", StringComparison.Ordinal));
        Assert.AreEqual("Žuta pošta", (await server.GetStoredEmailAsync(DefaultFolders.Inbox)).Subject);

        var invalidHeaderPrefix = Encoding.ASCII.GetBytes(
            "From: user@mk8n.com\r\nTo: user@mk8n.com\r\nSubject: ");
        var invalidHeaderMessage = invalidHeaderPrefix
            .Concat(new byte[] { 0xc3, 0x28 })
            .Concat(Encoding.ASCII.GetBytes("\r\n\r\nbody\r\n"))
            .ToArray();
        await connection.WriteLineAsync($"a14 APPEND INBOX {{{invalidHeaderMessage.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteBytesAsync(invalidHeaderMessage);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a14 NO [CANNOT]", StringComparison.Ordinal));

        await connection.WriteLineAsync("a15 SELECT INBOX");
        await ReadUntilTaggedResponseAsync(connection, "a15");
        await connection.WriteLineAsync("a16 UID SEARCH CHARSET UTF-8 ALL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a16 BAD", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("a17 UID SEARCH SUBJECT \"Žuta\"");
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a17 OK", StringComparison.Ordinal));

        await connection.WriteUtf8LineAsync("a18 SELECT \"Projects/Žuta pošta\"");
        var selected = await ReadUntilTaggedResponseAsync(connection, "a18");
        Assert.IsTrue(selected[^1].StartsWith("a18 OK [READ-WRITE]", StringComparison.Ordinal));
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
    [Timeout(20_000)]
    public async Task ImapSearchResSavesUidStableResultsAcrossCommandsAndExpunges()
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
        await ReadUntilTaggedResponseAsync(connection, "a3");

        await connection.WriteLineAsync(
            "a4 UID SEARCH RETURN (SAVE) OR SUBJECT MixedCaseSubject SUBJECT \"Other subject\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 SEARCH $");
        Assert.AreEqual("* SEARCH 1 2", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a6 UID SEARCH UID $");
        Assert.AreEqual("* SEARCH 1 2", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 UID SEARCH RETURN (SAVE MIN MAX) ALL");
        Assert.AreEqual(
            "* ESEARCH (TAG \"a7\") UID MIN 1 MAX 3",
            await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a7 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a8 FETCH $ (UID)");
        var fetched = await ReadUntilTaggedResponseAsync(connection, "a8");
        Assert.IsTrue(fetched.Any(line => line.StartsWith("* 1 FETCH (UID 1", StringComparison.Ordinal)));
        Assert.IsTrue(fetched.Any(line => line.StartsWith("* 3 FETCH (UID 3", StringComparison.Ordinal)));
        Assert.AreEqual(3, fetched.Count);

        await connection.WriteLineAsync("a9 STORE $ +FLAGS.SILENT (\\Flagged)");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a9 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a10 UID SEARCH FLAGGED");
        Assert.AreEqual("* SEARCH 1 3", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a10 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 UID STORE 1 +FLAGS.SILENT (\\Deleted)");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a11 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a12 EXPUNGE");
        Assert.AreEqual("* 1 EXPUNGE", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a12 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a13 UID FETCH $ (UID)");
        fetched = await ReadUntilTaggedResponseAsync(connection, "a13");
        Assert.AreEqual(2, fetched.Count);
        Assert.IsTrue(fetched[0].StartsWith("* 2 FETCH (UID 3", StringComparison.Ordinal));

        await connection.WriteLineAsync("a14 UID SEARCH RETURN (SAVE) SUBJECT absent-marker");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a14 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a15 COPY $ Trash");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a15 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a16 UID FETCH $ (UID)");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a16 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a17 UID SEARCH RETURN (SAVE) UID 2");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a17 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a18 UID SEARCH RETURN (SAVE UNKNOWN) ALL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a18 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a19 UID FETCH $ (UID)");
        fetched = await ReadUntilTaggedResponseAsync(connection, "a19");
        Assert.AreEqual(2, fetched.Count);
        Assert.IsTrue(fetched[0].StartsWith("* 1 FETCH (UID 2", StringComparison.Ordinal));

        await connection.WriteLineAsync("a20 UID SEARCH RETURN (SAVE) CHARSET KOI8-R ALL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith(
            "a20 NO [BADCHARSET",
            StringComparison.Ordinal));
        await connection.WriteLineAsync("a21 UID FETCH $ (UID)");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a21 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a22 UID SEARCH RETURN (SAVE) UID 2");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a22 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a23 SELECT INBOX");
        await ReadUntilTaggedResponseAsync(connection, "a23");
        await connection.WriteLineAsync("a24 UID FETCH $ (UID)");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a24 OK", StringComparison.Ordinal));
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
    [Timeout(20_000)]
    public async Task ImapSortImplementsEveryRfc5256KeyCharsetAndTieBreak()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedInboxMessagesForSortAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX");
        await ReadUntilTaggedResponseAsync(connection, "a3");

        await AssertSortAsync("a4 SORT (ARRIVAL) US-ASCII ALL", "* SORT 2 4 3 1 5");
        await AssertSortAsync("a5 UID SORT (DATE) US-ASCII ALL", "* SORT 30 40 10 50 20");
        await AssertSortAsync("a6 UID SORT (FROM) \"US-ASCII\" ALL", "* SORT 20 40 30 10 50");
        await AssertSortAsync("a7 UID SORT (TO) US-ASCII ALL", "* SORT 30 10 50 40 20");
        await AssertSortAsync("a8 UID SORT (CC) US-ASCII ALL", "* SORT 30 40 20 10 50");
        await AssertSortAsync("a9 UID SORT (SUBJECT) UTF-8 ALL", "* SORT 40 30 10 20 50");
        await AssertSortAsync("a10 UID SORT (SIZE) US-ASCII ALL", "* SORT 20 40 30 10 50");
        await AssertSortAsync("a11 UID SORT (REVERSE SIZE) US-ASCII ALL", "* SORT 10 50 30 40 20");
        await AssertSortAsync(
            "a12 UID SORT (SUBJECT REVERSE DATE) UTF-8 SUBJECT topic",
            "* SORT 20 10 50");
        await AssertSortAsync(
            "a12e UID SORT (DATE) US-ASCII SUBJECT absent-marker",
            "* SORT");

        await connection.WriteUtf8LineAsync(
            "a13 UID SORT (ARRIVAL) UTF-8 SUBJECT \"Äpfel\"");
        Assert.AreEqual("* SORT 30", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a13 OK", StringComparison.Ordinal));

        await AssertSortAsync(
            "a13m UID SORT (ARRIVAL) US-ASCII MODSEQ 3",
            "* SORT 40 30 50 (MODSEQ 5)");
        await connection.WriteLineAsync("a13s UID SEARCH MODSEQ 4");
        Assert.AreEqual("* SEARCH 40 50 (MODSEQ 5)", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a13s OK", StringComparison.Ordinal));
        await connection.WriteLineAsync(
            "a13x UID SEARCH RETURN (ALL) MODSEQ \"/flags/\\\\Seen\" all 4");
        Assert.AreEqual(
            "* ESEARCH (TAG \"a13x\") UID ALL 40,50 MODSEQ 5",
            await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a13x OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a13z UID SEARCH MODSEQ 0");
        Assert.AreEqual(
            "* SEARCH 10 20 30 40 50 (MODSEQ 5)",
            await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a13z OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a14 UID SORT () US-ASCII ALL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a14 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a15 UID SORT (REVERSE) US-ASCII ALL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a15 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a16 UID SORT (UNKNOWN) US-ASCII ALL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a16 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a17 UID SORT (DATE) KOI8-R ALL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith(
            "a17 NO [BADCHARSET (US-ASCII UTF-8)]",
            StringComparison.Ordinal));
        await connection.WriteLineAsync("a18 NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a18 OK", StringComparison.Ordinal));

        async Task AssertSortAsync(string command, string expected)
        {
            await connection.WriteLineAsync(command);
            Assert.AreEqual(expected, await connection.ReadLineAsync());
            var tag = command[..command.IndexOf(' ')];
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith(
                $"{tag} OK",
                StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task ImapOrderedSubjectThreadingUsesBaseSubjectSentDateAndSiblingBranches()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedInboxMessagesForSortAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX");
        await ReadUntilTaggedResponseAsync(connection, "a3");

        await AssertThreadAsync(
            "a4 THREAD ORDEREDSUBJECT US-ASCII ALL",
            "* THREAD (3)(4)(1 (5)(2))");
        await AssertThreadAsync(
            "a5 UID THREAD ORDEREDSUBJECT \"US-ASCII\" ALL",
            "* THREAD (30)(40)(10 (50)(20))");
        await AssertThreadAsync(
            "a6 UID THREAD ORDEREDSUBJECT UTF-8 SUBJECT topic",
            "* THREAD (10 (50)(20))");
        await connection.WriteUtf8LineAsync(
            "a7 UID THREAD ORDEREDSUBJECT UTF-8 SUBJECT \"Äpfel\"");
        Assert.AreEqual("* THREAD (30)", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a7 OK", StringComparison.Ordinal));
        await AssertThreadAsync(
            "a8 UID THREAD ORDEREDSUBJECT US-ASCII MODSEQ 4",
            "* THREAD (40)(50)");
        await AssertThreadAsync(
            "a8b UID THREAD ORDEREDSUBJECT US-ASCII UID 10,20",
            "* THREAD (10 20)");
        await AssertThreadAsync(
            "a9 UID THREAD ORDEREDSUBJECT US-ASCII SUBJECT absent-marker",
            "* THREAD");

        await connection.WriteLineAsync("a10 UID THREAD ORDEREDSUBJECT KOI8-R ALL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith(
            "a10 NO [BADCHARSET (US-ASCII UTF-8)]",
            StringComparison.Ordinal));
        await connection.WriteLineAsync("a11 UID THREAD UNKNOWN US-ASCII ALL");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a11 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a12 UID THREAD ORDEREDSUBJECT US-ASCII");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a12 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a13 NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a13 OK", StringComparison.Ordinal));

        async Task AssertThreadAsync(string command, string expected)
        {
            await connection.WriteLineAsync(command);
            Assert.AreEqual(expected, await connection.ReadLineAsync());
            var tag = command[..command.IndexOf(' ')];
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith(
                $"{tag} OK",
                StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ImapReferencesThreadingImplementsTheCompleteContainerAlgorithm()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedInboxMessagesForReferencesAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX");
        await ReadUntilTaggedResponseAsync(connection, "a3");

        await AssertThreadAsync(
            "a4 UID THREAD REFERENCES US-ASCII UID 101:119",
            "* THREAD (101 (103)(102))((104)(105))(106 107)(108)" +
            "(110 109)(111 113)(112)(114)(115)(117 116)(118 119)");
        await AssertThreadAsync(
            "a5 THREAD REFERENCES US-ASCII UID 101:119",
            "* THREAD (1 (3)(2))((4)(5))(6 7)(8)(10 9)" +
            "(11 13)(12)(14)(15)(17 16)(18 19)");
        await AssertThreadAsync(
            "a6 UID THREAD REFERENCES US-ASCII UID 102:103",
            "* THREAD ((103)(102))");
        await AssertThreadAsync(
            "a7 UID THREAD REFERENCES US-ASCII UID 102",
            "* THREAD (102)");
        await AssertThreadAsync(
            "a8 UID THREAD REFERENCES US-ASCII UID 201:203",
            "* THREAD ((201 202)(203))");
        await AssertThreadAsync(
            "a9 UID THREAD REFERENCES US-ASCII UID 204:205",
            "* THREAD (205 204)");
        await AssertThreadAsync(
            "a10 UID THREAD REFERENCES US-ASCII UID 206:207",
            "* THREAD (206)(207)");
        await AssertThreadAsync(
            "a11 UID THREAD REFERENCES US-ASCII UID 208:212",
            "* THREAD ((208)(209)(210)(211)(212))");
        await connection.WriteUtf8LineAsync(
            "a12 UID THREAD REFERENCES UTF-8 SUBJECT \"Äpfel\"");
        Assert.AreEqual("* THREAD (118 119)", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a12 OK", StringComparison.Ordinal));
        await AssertThreadAsync(
            "a13 UID THREAD REFERENCES US-ASCII UID 118:119 MODSEQ 18",
            "* THREAD (118 119)");
        await AssertThreadAsync(
            "a14 UID THREAD REFERENCES US-ASCII SUBJECT absent-marker",
            "* THREAD");
        await connection.WriteLineAsync("a15 NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a15 OK", StringComparison.Ordinal));

        async Task AssertThreadAsync(string command, string expected)
        {
            await connection.WriteLineAsync(command);
            Assert.AreEqual(expected, await connection.ReadLineAsync());
            var tag = command[..command.IndexOf(' ')];
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith(
                $"{tag} OK",
                StringComparison.Ordinal));
        }
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
        await connection.WriteLineAsync("a3 ENABLE UTF8=ACCEPT");
        Assert.AreEqual("* ENABLED UTF8=ACCEPT", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK", StringComparison.Ordinal));

        var messageSize = Encoding.UTF8.GetByteCount(message);
        await connection.WriteLineAsync($"a4 APPEND \"Sent\" {{{messageSize}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteUtf8RawAsync(message);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK [APPENDUID", StringComparison.Ordinal));

        var stored = await server.GetStoredEmailAsync(DefaultFolders.Sent);
        Assert.AreEqual("café", stored.Subject);
        Assert.AreEqual("\r\nbody é\r\n", stored.Body);
        Assert.AreEqual(messageSize, stored.SizeBytes);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(message), stored.RawMessage!);
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
    [Timeout(15_000)]
    public async Task ImapPersistsCustomKeywordsAcrossAppendStoreSearchAndCopy()
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
            "Subject: keyword persistence\r\n" +
            "\r\n" +
            "tagged message\r\n";
        await connection.WriteLineAsync(
            $"a3 APPEND \"Sent\" (\\Seen $label1 Custom) {{{message.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 SELECT \"Sent\"");
        var responses = await ReadUntilTaggedResponseAsync(connection, "a4");
        var definedFlags = responses.Single(line => line.StartsWith("* FLAGS (", StringComparison.Ordinal));
        StringAssert.Contains(definedFlags, "$label1");
        StringAssert.Contains(definedFlags, "Custom");
        Assert.IsTrue(responses.Any(line => line.Contains("PERMANENTFLAGS", StringComparison.Ordinal)
            && line.Contains("\\*", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a5 FETCH 1 (UID FLAGS)");
        responses = await ReadUntilTaggedResponseAsync(connection, "a5");
        Assert.IsTrue(responses.Any(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal)
            && line.Contains("\\Seen", StringComparison.Ordinal)
            && line.Contains("$label1", StringComparison.Ordinal)
            && line.Contains("Custom", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a6 STORE 1 +FLAGS ($label2)");
        responses = await ReadUntilTaggedResponseAsync(connection, "a6");
        Assert.IsTrue(responses.Any(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal)
            && line.Contains("$label1", StringComparison.Ordinal)
            && line.Contains("$label2", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a7 UID STORE 1 -FLAGS (CUSTOM)");
        responses = await ReadUntilTaggedResponseAsync(connection, "a7");
        Assert.IsTrue(responses.Any(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal)
            && line.Contains("$label1", StringComparison.Ordinal)
            && line.Contains("$label2", StringComparison.Ordinal)
            && !line.Contains("Custom", StringComparison.OrdinalIgnoreCase)));

        await connection.WriteLineAsync("a8 UID SEARCH KEYWORD $label2");
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a8 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a9 COPY 1 Trash");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a9 OK [COPYUID", StringComparison.Ordinal));
        await connection.WriteLineAsync("a10 SELECT Trash");
        await ReadUntilTaggedResponseAsync(connection, "a10");
        await connection.WriteLineAsync("a11 FETCH 1 FLAGS");
        responses = await ReadUntilTaggedResponseAsync(connection, "a11");
        Assert.IsTrue(responses.Any(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal)
            && line.Contains("$label1", StringComparison.Ordinal)
            && line.Contains("$label2", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a12 STORE 1 FLAGS (\\Seen Final)");
        responses = await ReadUntilTaggedResponseAsync(connection, "a12");
        var replacement = responses.Single(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal));
        StringAssert.Contains(replacement, "\\Seen");
        StringAssert.Contains(replacement, "Final");
        Assert.IsFalse(replacement.Contains("$label", StringComparison.Ordinal));

        await connection.WriteLineAsync("a13 UID SEARCH UNKEYWORD Final");
        Assert.AreEqual("* SEARCH", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a13 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a14 STORE 1 +FLAGS (bad])");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a14 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync($"a15 APPEND \"Sent\" (\\Recent) {{{message.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a15 NO [CANNOT]", StringComparison.Ordinal));

        var excessiveKeywords = string.Join(' ', Enumerable.Range(1, 129).Select(index => $"k{index}"));
        await connection.WriteLineAsync($"a16 STORE 1 +FLAGS ({excessiveKeywords})");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a16 NO [LIMIT]", StringComparison.Ordinal));

        await connection.WriteLineAsync("a17 FETCH 1 FLAGS");
        responses = await ReadUntilTaggedResponseAsync(connection, "a17");
        replacement = responses.Single(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal));
        StringAssert.Contains(replacement, "Final");
        Assert.IsFalse(replacement.Contains("bad]", StringComparison.Ordinal));
        Assert.IsFalse(replacement.Contains("k1", StringComparison.Ordinal));

        var sent = await server.GetStoredEmailAsync(DefaultFolders.Sent);
        CollectionAssert.AreEquivalent(new[] { "$label1", "$label2" }, sent.Keywords);
        var copied = await server.GetStoredEmailAsync(DefaultFolders.Trash);
        CollectionAssert.AreEqual(new[] { "Final" }, copied.Keywords);
        Assert.IsTrue(copied.IsRead);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(25_000)]
    public async Task ImapIdleReportsCrossConnectionMailboxChanges(bool enableQresync)
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var writerConnection = await ProtocolConnection.ConnectAsync(port);
        await using var idleConnection = await ProtocolConnection.ConnectAsync(port);

        await writerConnection.ReadLineAsync();
        await writerConnection.WriteLineAsync("w1 STARTTLS");
        Assert.IsTrue((await writerConnection.ReadLineAsync()).StartsWith("w1 OK", StringComparison.Ordinal));
        await writerConnection.UpgradeToTlsAsync("email.mk8n.com");
        await writerConnection.WriteLineAsync($"w2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await writerConnection.ReadLineAsync()).StartsWith("w2 OK", StringComparison.Ordinal));

        await idleConnection.ReadLineAsync();
        await idleConnection.WriteLineAsync("r1 STARTTLS");
        Assert.IsTrue((await idleConnection.ReadLineAsync()).StartsWith("r1 OK", StringComparison.Ordinal));
        await idleConnection.UpgradeToTlsAsync("email.mk8n.com");
        await idleConnection.WriteLineAsync($"r2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await idleConnection.ReadLineAsync()).StartsWith("r2 OK", StringComparison.Ordinal));

        const string firstMessage =
            "From: user@mk8n.com\r\nTo: user@mk8n.com\r\n" +
            "Subject: first idle message\r\n\r\nfirst\r\n";
        const string secondMessage =
            "From: user@mk8n.com\r\nTo: user@mk8n.com\r\n" +
            "Subject: second idle message\r\n\r\nsecond\r\n";
        const string thirdMessage =
            "From: user@mk8n.com\r\nTo: user@mk8n.com\r\n" +
            "Subject: third idle message\r\n\r\nthird\r\n";

        await writerConnection.WriteLineAsync($"w3 APPEND INBOX {{{firstMessage.Length}}}");
        Assert.IsTrue((await writerConnection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await writerConnection.WriteRawAsync(firstMessage);
        await writerConnection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await writerConnection.ReadLineAsync()).StartsWith("w3 OK", StringComparison.Ordinal));
        await writerConnection.WriteLineAsync($"w4 APPEND INBOX {{{secondMessage.Length}}}");
        Assert.IsTrue((await writerConnection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await writerConnection.WriteRawAsync(secondMessage);
        await writerConnection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await writerConnection.ReadLineAsync()).StartsWith("w4 OK", StringComparison.Ordinal));

        var selectTag = "r3";
        var idleTag = "r4";
        if (enableQresync)
        {
            await idleConnection.WriteLineAsync("r3 ENABLE QRESYNC");
            Assert.AreEqual("* ENABLED QRESYNC", await idleConnection.ReadLineAsync());
            Assert.IsTrue((await idleConnection.ReadLineAsync()).StartsWith("r3 OK", StringComparison.Ordinal));
            selectTag = "r4";
            idleTag = "r5";
        }

        await idleConnection.WriteLineAsync($"{selectTag} SELECT INBOX (CONDSTORE)");
        var selected = await ReadUntilTaggedResponseAsync(idleConnection, selectTag);
        Assert.IsTrue(selected[^1].StartsWith($"{selectTag} OK", StringComparison.Ordinal));
        await idleConnection.WriteLineAsync($"{idleTag} IDLE");
        Assert.AreEqual("+ idling", await idleConnection.ReadLineAsync());

        await writerConnection.WriteLineAsync("w5 SELECT INBOX");
        await ReadUntilTaggedResponseAsync(writerConnection, "w5");
        await writerConnection.WriteLineAsync("w6 STORE 1 +FLAGS (\\Seen $label1)");
        await ReadUntilTaggedResponseAsync(writerConnection, "w6");
        await writerConnection.WriteLineAsync("w7 MOVE 2 Trash");
        await ReadUntilTaggedResponseAsync(writerConnection, "w7");
        await writerConnection.WriteLineAsync($"w8 APPEND INBOX {{{thirdMessage.Length}}}");
        Assert.IsTrue((await writerConnection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await writerConnection.WriteRawAsync(thirdMessage);
        await writerConnection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await writerConnection.ReadLineAsync()).StartsWith("w8 OK", StringComparison.Ordinal));

        var expectedRemoval = enableQresync ? "* VANISHED 2" : "* 2 EXPUNGE";
        var updates = new List<string>();
        for (var index = 0; index < 10; index++)
        {
            updates.Add(await idleConnection.ReadLineAsync(TimeSpan.FromSeconds(8)));
            if (updates.Any(line => line == expectedRemoval)
                && updates.Any(line => line == "* 2 EXISTS")
                && updates.Any(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal)
                    && line.Contains("\\Seen", StringComparison.Ordinal)
                    && line.Contains("$label1", StringComparison.Ordinal)
                    && line.Contains("MODSEQ", StringComparison.Ordinal))
                && updates.Any(line => line.StartsWith("* 2 FETCH", StringComparison.Ordinal)))
            {
                break;
            }
        }

        CollectionAssert.Contains(updates, expectedRemoval);
        CollectionAssert.Contains(updates, "* 2 EXISTS");
        Assert.IsTrue(updates.Any(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal)
            && line.Contains("\\Seen", StringComparison.Ordinal)
            && line.Contains("$label1", StringComparison.Ordinal)
            && line.Contains("MODSEQ", StringComparison.Ordinal)));
        Assert.IsTrue(updates.Any(line => line.StartsWith("* 2 FETCH", StringComparison.Ordinal)));

        await idleConnection.WriteLineAsync("DONE");
        var completed = await ReadUntilTaggedResponseAsync(idleConnection, idleTag);
        Assert.IsTrue(completed[^1].StartsWith($"{idleTag} OK", StringComparison.Ordinal));
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
            "Content-Type: application/octet-stream\r\n" +
            "Content-Disposition: attachment; filename=\"test.txt\"\r\n" +
            "Content-ID: <attachment@example.net>\r\n" +
            "Content-MD5: dGVzdC1kaWdlc3Q=\r\n" +
            "Content-Language: en, fr\r\n" +
            "Content-Location: files/test.txt\r\n" +
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
        StringAssert.Contains(bodyStructure, "(\"BOUNDARY\" \"mix\")");
        StringAssert.Contains(bodyStructure, "\"<attachment@example.net>\"");
        StringAssert.Contains(bodyStructure, "\"dGVzdC1kaWdlc3Q=\"");
        StringAssert.Contains(bodyStructure, "(\"ATTACHMENT\" (\"FILENAME\" \"test.txt\"))");
        StringAssert.Contains(bodyStructure, "(\"en\" \"fr\")");
        StringAssert.Contains(bodyStructure, "\"files/test.txt\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 UID FETCH 1 (UID BODY)");
        var body = await connection.ReadLineAsync();
        StringAssert.Contains(body, "BODY");
        StringAssert.Contains(body, "\"<attachment@example.net>\"");
        Assert.IsFalse(body.Contains("\"BOUNDARY\"", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("\"ATTACHMENT\"", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("dGVzdC1kaWdlc3Q=", StringComparison.Ordinal));
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 UID FETCH 1 (UID BODY.PEEK[1.2])");
        var htmlSection = string.Join('\n', await ReadUntilTaggedResponseAsync(connection, "a7"));
        StringAssert.Contains(htmlSection, "BODY[1.2]");
        StringAssert.Contains(htmlSection, "<html><body>html=20body</body></html>");
        Assert.IsFalse((await server.GetStoredEmailByUidAsync(1)).IsRead);

        await connection.WriteLineAsync("a8 UID FETCH 1 (UID BODY.PEEK[2.MIME])");
        var attachmentHeaders = string.Join('\n', await ReadUntilTaggedResponseAsync(connection, "a8"));
        StringAssert.Contains(attachmentHeaders, "BODY[2.MIME]");
        StringAssert.Contains(attachmentHeaders, "Content-Type: application/octet-stream");
        StringAssert.Contains(attachmentHeaders, "Content-Disposition: attachment");
        Assert.IsFalse((await server.GetStoredEmailByUidAsync(1)).IsRead);

        await connection.WriteLineAsync("a9 UID FETCH 1 (UID BODY[2])");
        var attachment = string.Join('\n', await ReadUntilTaggedResponseAsync(connection, "a9"));
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

    private static async Task<List<string>> ReadPop3MultilineAsync(ProtocolConnection connection)
    {
        var response = new List<string>();
        while (true)
        {
            var line = await connection.ReadLineAsync();
            if (line == ".")
                return response;
            response.Add(line);
        }
    }

    private EnvironmentConfig CreateEnvironment(
        int? smtpPort = null,
        int? submissionPort = null,
        int? imapPort = null,
        int? pop3Port = null,
        int? smtpImplicitTlsPort = null,
        int? imapImplicitTlsPort = null,
        int? pop3ImplicitTlsPort = null,
        string? certificatePath = null,
        int connectionTimeoutSeconds = 10,
        bool enableOAuth = false)
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
            Pop3 = new Pop3Config
            {
                Port = pop3Port ?? ReservePort(),
                ImplicitTlsPort = pop3ImplicitTlsPort ?? ReservePort(),
                EnablePop3 = pop3Port.HasValue,
                EnableImplicitTls = pop3ImplicitTlsPort.HasValue,
                EnableStartTls = true,
            },
            OAuth = new OAuthConfig
            {
                EnableOAuth = enableOAuth,
                PublicBaseUrl = "https://email.mk8n.com",
                ClientId = "thunderbird",
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

    private static string CreateXOAuth2Response(string accessToken) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"user={TestUsername}\u0001auth=Bearer {accessToken}\u0001\u0001"));

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
            var (services, emailService, mailQueue) = CreateServices(environment);
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
            var (services, emailService, mailQueue) = CreateServices(environment);
            var hostedService = new ImapServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                logger ?? NullLogger<ImapServerService>.Instance);
            var fixture = new ServerFixture(services, hostedService, emailService, mailQueue);
            await fixture.StartAsync(port);
            return fixture;
        }

        public static async Task<ServerFixture> StartPop3Async(
            EnvironmentConfig environment,
            int port,
            ILogger<Pop3ServerService>? logger = null)
        {
            var (services, emailService, mailQueue) = CreateServices(environment);
            var hostedService = new Pop3ServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                logger ?? NullLogger<Pop3ServerService>.Instance);
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

        public async Task<int> CountExpungedUidsAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            return await database.ExpungedUids.CountAsync();
        }

        public async Task<string> CreateOAuthAccessTokenAsync(string scopeName)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var userId = await database.Users
                .Where(user => user.Username == TestUsername)
                .Select(user => user.Id)
                .SingleAsync();
            var pair = await scope.ServiceProvider.GetRequiredService<IOAuthTokenService>()
                .CreateGrantAsync(
                    userId,
                    "thunderbird",
                    "Thunderbird protocol test",
                    ["offline_access", scopeName]);
            return pair?.AccessToken
                ?? throw new InvalidOperationException("The OAuth access token was not created.");
        }

        private static (
            ServiceProvider Services,
            StubEmailService EmailService,
            StubMailSubmissionQueue MailQueue) CreateServices(EnvironmentConfig environment)
        {
            var emailService = new StubEmailService();
            var mailQueue = new StubMailSubmissionQueue();
            var databaseName = $"transport-{Guid.NewGuid():N}";
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton(environment);
            serviceCollection.AddSingleton<IEmailService>(emailService);
            serviceCollection.AddSingleton<IMailSubmissionQueue>(mailQueue);
            serviceCollection.AddScoped<ISenderAuthorizationService, SenderAuthorizationService>();
            serviceCollection.AddScoped<IMailAuthenticator, MailAuthenticator>();
            serviceCollection.AddScoped<IOAuthTokenService, OAuthTokenService>();
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

        public async Task SeedPop3MessagesAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Inbox);
            var firstRaw =
                $"From: sender@example.net\r\nTo: {TestUsername}\r\nSubject: POP first\r\n\r\n" +
                ".leading dot\r\nsecond body line\r\n";
            var secondRaw =
                $"From: sender@example.net\nTo: {TestUsername}\nSubject: POP second\n\n" +
                "second message without canonical endings";
            database.Emails.AddRange(
                new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Sender = "sender@example.net",
                    Recipient = TestUsername,
                    Subject = "POP first",
                    Body = ".leading dot\r\nsecond body line\r\n",
                    RawHeaders = $"From: sender@example.net\r\nTo: {TestUsername}\r\nSubject: POP first",
                    RawMessage = MailWireEncoding.Instance.GetBytes(firstRaw),
                    SizeBytes = MailWireEncoding.Instance.GetByteCount(firstRaw),
                    Uid = 1,
                    ModSeq = 1,
                    FolderId = folder.Id,
                    ReceivedAt = DateTime.UtcNow.AddMinutes(-1),
                },
                new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Sender = "sender@example.net",
                    Recipient = TestUsername,
                    Subject = "POP second",
                    Body = "second message without canonical endings",
                    RawHeaders = $"From: sender@example.net\nTo: {TestUsername}\nSubject: POP second",
                    RawMessage = MailWireEncoding.Instance.GetBytes(secondRaw),
                    SizeBytes = MailWireEncoding.Instance.GetByteCount(secondRaw),
                    Uid = 2,
                    ModSeq = 2,
                    FolderId = folder.Id,
                    ReceivedAt = DateTime.UtcNow,
                });
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

        public async Task SeedInboxMessagesForSortAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Inbox);
            database.Emails.AddRange(
                CreateSortEmail(
                    folder.Id,
                    uid: 10,
                    new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
                    size: 400,
                    subject: "Re: [list] Topic (fwd)",
                    "From: Zulu Person <zeta@example.net>\r\n" +
                    "To: Bravo Person <bravo@example.net>\r\n" +
                    "Cc: Delta Person <delta@example.net>\r\n" +
                    "Date: Mon, 2 Feb 2026 10:00:00 +0000\r\n" +
                    "Subject: Re: [list] Topic (fwd)"),
                CreateSortEmail(
                    folder.Id,
                    uid: 20,
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    size: 100,
                    subject: "topic",
                    "From: Alpha Person <alpha@example.net>\r\n" +
                    "To: Delta Person <delta@example.net>\r\n" +
                    "Cc: Charlie Person <charlie@example.net>\r\n" +
                    "Date: Tue, 3 Feb 2026 10:00:00 +0000\r\n" +
                    "Subject: topic"),
                CreateSortEmail(
                    folder.Id,
                    uid: 30,
                    new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                    size: 300,
                    subject: "Re: Äpfel",
                    "From: Charlie Person <charlie@example.net>\r\n" +
                    "To: Alpha Person <alpha@example.net>\r\n" +
                    "Subject: =?UTF-8?Q?Re=3A_=C3=84pfel?="),
                CreateSortEmail(
                    folder.Id,
                    uid: 40,
                    new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    size: 200,
                    subject: "Apfel",
                    "From: Bravo Person <bravo@example.net>\r\n" +
                    "To: Charlie Person <charlie@example.net>\r\n" +
                    "Cc: Alpha Person <alpha@example.net>\r\n" +
                    "Date: Sun, 1 Feb 2026 10:00:00 +0000\r\n" +
                    "Subject: Apfel"),
                CreateSortEmail(
                    folder.Id,
                    uid: 50,
                    new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
                    size: 400,
                    subject: "Fwd: Topic",
                    "From: Zulu Person <zeta@example.net>\r\n" +
                    "To: Bravo Person <bravo@example.net>\r\n" +
                    "Cc: Delta Person <delta@example.net>\r\n" +
                    "Date: Mon, 2 Feb 2026 10:00:00 +0000\r\n" +
                    "Subject: Fwd: Topic"));
            folder.NextUid = 51;
            folder.HighestModSeq = 5;
            await database.SaveChangesAsync();
        }

        public async Task SeedInboxMessagesForReferencesAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Inbox);
            database.Emails.AddRange(
                CreateThreadEmail(folder.Id, 101, 1, "Project", "<root@example.net>"),
                CreateThreadEmail(folder.Id, 102, 3, "Re: Project", "<reply@example.net>",
                    references: "<root@example.net>"),
                CreateThreadEmail(folder.Id, 103, 2, "Re: Project", "<branch@example.net>",
                    references: "<root@example.net>"),
                CreateThreadEmail(folder.Id, 104, 4, "Orphan A", "<orphan-a@example.net>",
                    references: "<missing@example.net>"),
                CreateThreadEmail(folder.Id, 105, 5, "Orphan B", "<orphan-b@example.net>",
                    references: "<missing@example.net>"),
                CreateThreadEmail(folder.Id, 106, 6, "Quoted root",
                    """<"quoted"@example.net>"""),
                CreateThreadEmail(folder.Id, 107, 7, "Quoted child", "<quoted-child@example.net>",
                    references: "<quoted@example.net>"),
                CreateThreadEmail(folder.Id, 108, 8, "Duplicate", "<root@example.net>"),
                CreateThreadEmail(folder.Id, 109, 10, "Fallback child", "<fallback-child@example.net>",
                    references: "not-a-message-id", inReplyTo: "<fallback@example.net>"),
                CreateThreadEmail(folder.Id, 110, 9, "Fallback parent", "<fallback@example.net>"),
                CreateThreadEmail(folder.Id, 111, 11, "Preferred parent", "<preferred@example.net>"),
                CreateThreadEmail(folder.Id, 112, 12, "Ignored parent", "<ignored@example.net>"),
                CreateThreadEmail(folder.Id, 113, 13, "Re: Preferred parent",
                    "<preferred-child@example.net>", references: "<preferred@example.net>",
                    inReplyTo: "<ignored@example.net>"),
                CreateThreadEmail(folder.Id, 114, 14, "Upper case ID", "<Case@example.net>"),
                CreateThreadEmail(folder.Id, 115, 15, "Lower case reference",
                    "<case-child@example.net>", references: "<case@example.net>"),
                CreateThreadEmail(folder.Id, 116, 16, "Loop A", "<loop-a@example.net>",
                    references: "<loop-b@example.net>"),
                CreateThreadEmail(folder.Id, 117, 17, "Loop B", "<loop-b@example.net>",
                    references: "<loop-a@example.net>"),
                CreateThreadEmail(folder.Id, 118, 18, "Äpfel", "<unicode-root@example.net>"),
                CreateThreadEmail(folder.Id, 119, 19, "Re: Äpfel", "<unicode-child@example.net>",
                    references: "<unicode-root@example.net>"),
                CreateThreadEmail(folder.Id, 201, 20, "Topic", "<subject-one@example.net>"),
                CreateThreadEmail(folder.Id, 202, 21, "Re: topic", "<subject-two@example.net>"),
                CreateThreadEmail(folder.Id, 203, 22, "TOPIC", "<subject-three@example.net>"),
                CreateThreadEmail(folder.Id, 204, 23, "Re: Promote", "<promote-reply@example.net>"),
                CreateThreadEmail(folder.Id, 205, 24, "Promote", "<promote-original@example.net>"),
                CreateThreadEmail(folder.Id, 206, 25, "Re:", "<empty-one@example.net>"),
                CreateThreadEmail(folder.Id, 207, 26, "Fwd:", "<empty-two@example.net>"),
                CreateThreadEmail(folder.Id, 208, 27, "Shared", "<dummy-one@example.net>",
                    references: "<missing-a@example.net>"),
                CreateThreadEmail(folder.Id, 209, 28, "Other A", "<dummy-two@example.net>",
                    references: "<missing-a@example.net>"),
                CreateThreadEmail(folder.Id, 210, 29, "Re: Shared", "<dummy-three@example.net>",
                    references: "<missing-b@example.net>"),
                CreateThreadEmail(folder.Id, 211, 30, "Other B", "<dummy-four@example.net>",
                    references: "<missing-b@example.net>"),
                CreateThreadEmail(folder.Id, 212, 31, "SHARED", "<dummy-five@example.net>"));
            folder.NextUid = 213;
            folder.HighestModSeq = 31;
            await database.SaveChangesAsync();
        }

        private static EmailDB CreateSortEmail(
            Guid folderId,
            int uid,
            DateTime receivedAt,
            int size,
            string subject,
            string rawHeaders) => new()
        {
            Id = Guid.CreateVersion7(),
            Sender = "normalized-column-must-not-win@example.net",
            Recipient = "normalized-column-must-not-win@example.net",
            Cc = "normalized-column-must-not-win@example.net",
            Subject = subject,
            Body = "body\r\n",
            RawHeaders = rawHeaders,
            SizeBytes = size,
            Uid = uid,
            ModSeq = uid / 10,
            FolderId = folderId,
            ReceivedAt = receivedAt,
        };

        private static EmailDB CreateThreadEmail(
            Guid folderId,
            int uid,
            int sentDay,
            string subject,
            string messageId,
            string? references = null,
            string? inReplyTo = null)
        {
            var sentAt = new DateTimeOffset(
                2026,
                1,
                sentDay,
                12,
                0,
                0,
                TimeSpan.Zero);
            var headerSubject = subject switch
            {
                "Äpfel" => "=?UTF-8?Q?=C3=84pfel?=",
                "Re: Äpfel" => "=?UTF-8?Q?Re=3A_=C3=84pfel?=",
                _ => subject,
            };
            var headers = new StringBuilder()
                .Append("From: sender@example.net\r\n")
                .Append($"To: {TestUsername}\r\n")
                .Append($"Date: {sentAt:R}\r\n")
                .Append($"Subject: {headerSubject}\r\n")
                .Append($"Message-ID: {messageId}\r\n");
            if (references is not null)
                headers.Append($"References: {references}\r\n");
            if (inReplyTo is not null)
                headers.Append($"In-Reply-To: {inReplyTo}\r\n");

            return new EmailDB
            {
                Id = Guid.CreateVersion7(),
                Sender = "normalized-column-must-not-win@example.net",
                Recipient = TestUsername,
                Subject = subject,
                Body = "body\r\n",
                RawHeaders = headers.ToString().TrimEnd('\r', '\n'),
                MessageId = $"<column-{uid}@must-not-win.example>",
                InReplyTo = "<column-parent@must-not-win.example>",
                SizeBytes = 100,
                Uid = uid,
                ModSeq = uid < 200 ? uid - 100 : uid - 181,
                FolderId = folderId,
                ReceivedAt = new DateTime(
                    2027,
                    1,
                    32 - sentDay,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
            };
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

        public Task<string> ReadLineAsync() => ReadLineAsync(TimeSpan.FromSeconds(3));

        public async Task<string> ReadLineAsync(TimeSpan timeoutDuration)
        {
            using var timeout = new CancellationTokenSource(timeoutDuration);
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

        public Task WriteUtf8LineAsync(string line) => WriteUtf8RawAsync(line + "\r\n");

        public async Task WriteUtf8RawAsync(string value)
        {
            await _writer.FlushAsync();
            await _stream.WriteAsync(Encoding.UTF8.GetBytes(value));
            await _stream.FlushAsync();
        }

        public async Task WriteBytesAsync(ReadOnlyMemory<byte> value)
        {
            await _writer.FlushAsync();
            await _stream.WriteAsync(value);
            await _stream.FlushAsync();
        }

        public async Task<string> ReadUtf8LineAsync()
        {
            var wireValue = await ReadLineAsync();
            return new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetString(Encoding.Latin1.GetBytes(wireValue));
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

        public async Task UpgradeToDeflateAsync()
        {
            await _writer.FlushAsync();
            _reader.Dispose();
            await _writer.DisposeAsync();

            var transport = _stream;
            var inflater = new DeflateStream(transport, CompressionMode.Decompress, leaveOpen: true);
            var deflater = new DeflateStream(transport, CompressionLevel.Fastest, leaveOpen: true);
            _stream = new TestDuplexStream(inflater, deflater, transport);
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

    private sealed class TestDuplexStream(
        Stream readStream,
        Stream writeStream,
        Stream transport) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            readStream.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            readStream.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            writeStream.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            writeStream.WriteAsync(buffer, cancellationToken);

        public override void Flush() => writeStream.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            writeStream.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                readStream.Dispose();
                writeStream.Dispose();
                transport.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await readStream.DisposeAsync();
            await writeStream.DisposeAsync();
            await transport.DisposeAsync();
            GC.SuppressFinalize(this);
        }
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
            CancellationToken cancellationToken = default,
            IReadOnlyCollection<string>? flags = null,
            bool createFolder = false) =>
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
