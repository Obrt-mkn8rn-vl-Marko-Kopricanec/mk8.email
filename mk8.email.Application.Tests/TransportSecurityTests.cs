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
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Imap;
using mk8.email.Contracts.Pop3;
using mk8.email.Gateway.Protocols.Pop3;
using mk8.email.Imap.Presentation;
using mk8.email.Messaging;
using mk8.email.Smtp.Presentation;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed partial class TransportSecurityTests
{
    private const string TestUsername = "user@tenant.example.test";
    private const string TestPassword = "correct horse battery staple";
    // Public synthetic fixture credential only. Avoid generating the same costly
    // password hash on every setup; each login still uses production verification.
    private const string TestPasswordHash = "{BLF-CRYPT}$2a$13$ivgpgSaFWcZVeTw7rqOwaej4EtphTOZdwAXYnkw9vIs0INaqprf4S";

    private string _testDirectory = null!;
    private string _certificatePath = null!;

    [TestMethod]
    public void ProtocolFixturePasswordRetainsProductionHashPolicyAndVerification()
    {
        var policyPrefix = TestPasswordHash[..(PasswordHasher.BcryptSchemePrefix.Length + 7)];
        StringAssert.StartsWith(PasswordHasher.Hash(TestPassword), policyPrefix, StringComparison.Ordinal);
        Assert.IsTrue(PasswordHasher.Verify(TestPassword, TestPasswordHash));
        Assert.IsFalse(PasswordHasher.Verify("not-the-fixture-password", TestPasswordHash));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpGatewayPresentationJournalsWireBytesAndFailsClosed()
    {
        var port = ReservePort();
        var journal = new RecordingSmtpJournal();
        {
            var server = (await ServerFixture.StartSmtpAsync(
                         CreateEnvironment(smtpPort: port), port, journal: journal).ConfigureAwait(false));
            await using var serverLifetime = server.ConfigureAwait(false);
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
            await using var connectionLifetime = connection.ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("220 ", StringComparison.Ordinal));
            await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
            await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
            await connection.WriteLineAsync("QUIT").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("221 ", StringComparison.Ordinal));
        }

        var sessionId = journal.Records.Single(record => string.Equals(record.Direction, GatewayTrafficDirections.Inbound
, StringComparison.Ordinal) && Encoding.Latin1.GetString(record.Payload).Contains(
                "EHLO client.example\r\n", StringComparison.Ordinal)).SessionId;
        var records = journal.Records.Where(record => record.SessionId == sessionId).ToArray();
        Assert.IsTrue(records.Length >= 4);
        Assert.IsTrue(records.Where(record => string.Equals(record.Direction, GatewayTrafficDirections.Inbound, StringComparison.Ordinal))
            .Any(record => Encoding.Latin1.GetString(record.Payload).Contains(
                "EHLO client.example\r\n", StringComparison.Ordinal)));
        Assert.IsTrue(records.Where(record => string.Equals(record.Direction, GatewayTrafficDirections.Outbound, StringComparison.Ordinal))
            .Any(record => Encoding.Latin1.GetString(record.Payload).Contains(
                "220 ", StringComparison.Ordinal)));
        Assert.IsTrue(records.All(record => string.Equals(record.Protocol, "smtp", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, records.Length).Select(value => (long)value).ToArray(),
            records.Select(record => record.Sequence).ToArray());

        var rejectedPort = ReservePort();
        var rejectedServer = (await ServerFixture.StartSmtpAsync(
            CreateEnvironment(smtpPort: rejectedPort),
            rejectedPort,
            journal: new RecordingSmtpJournal { RejectWrites = true }).ConfigureAwait(false));
        await using var rejectedServerLifetime = rejectedServer.ConfigureAwait(false);
        var rejectedConnection = (await ProtocolConnection.ConnectAsync(rejectedPort).ConfigureAwait(false));
        await using var rejectedConnectionLifetime = rejectedConnection.ConfigureAwait(false);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => rejectedConnection.ReadLineAsync()).ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpKeepsTheGatewaySessionAliveWhenRecipientPolicyIsUnavailable()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartSmtpAsync(
            CreateEnvironment(smtpPort: port), port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        server.EmailService.ThrowOnCanReceive = true;
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("MAIL FROM:<sender@example.test>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<user@tenant.example.test>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("451 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Pop3GatewayPresentationJournalsWireBytesAndFailsClosed()
    {
        var port = ReservePort();
        var journal = new RecordingSmtpJournal();
        {
            var server = (await ServerFixture.StartPop3Async(
                         CreateEnvironment(pop3Port: port), port, journal: journal).ConfigureAwait(false));
            await using var serverLifetime = server.ConfigureAwait(false);
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
            await using var connectionLifetime = connection.ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
            await connection.WriteLineAsync("CAPA").ConfigureAwait(false);
            Assert.AreEqual("+OK Capability list follows", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
            while (!string.Equals(await connection.ReadLineAsync().ConfigureAwait(false), ".", StringComparison.Ordinal))
            {
            }
            await connection.WriteLineAsync("QUIT").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
        }

        var sessionId = journal.Records.Single(record => string.Equals(record.Direction, GatewayTrafficDirections.Inbound
, StringComparison.Ordinal) && Encoding.Latin1.GetString(record.Payload).Contains("CAPA\r\n", StringComparison.Ordinal))
            .SessionId;
        var records = journal.Records.Where(record => record.SessionId == sessionId).ToArray();
        Assert.IsTrue(records.Any(record => string.Equals(record.Direction, GatewayTrafficDirections.Outbound
, StringComparison.Ordinal) && Encoding.Latin1.GetString(record.Payload).Contains("+OK ", StringComparison.Ordinal)));
        Assert.IsTrue(records.All(record => string.Equals(record.Protocol, "pop3", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, records.Length).Select(value => (long)value).ToArray(),
            records.Select(record => record.Sequence).ToArray());

        var rejectedPort = ReservePort();
        var rejectedServer = (await ServerFixture.StartPop3Async(
            CreateEnvironment(pop3Port: rejectedPort),
            rejectedPort,
            journal: new RecordingSmtpJournal { RejectWrites = true }).ConfigureAwait(false));
        await using var rejectedServerLifetime = rejectedServer.ConfigureAwait(false);
        var rejectedConnection = (await ProtocolConnection.ConnectAsync(rejectedPort).ConfigureAwait(false));
        await using var rejectedConnectionLifetime = rejectedConnection.ConfigureAwait(false);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => rejectedConnection.ReadLineAsync()).ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Pop3GatewayKeepsConnectionAliveWhenApplicationIsUnavailable()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartPop3Async(
            CreateEnvironment(pop3Port: port),
            port,
            applicationService: new UnavailablePop3ApplicationService()).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("STLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"USER {TestUsername}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync($"PASS {TestPassword}").ConfigureAwait(false);
        Assert.AreEqual(
            "-ERR [SYS/TEMP] authentication service is unavailable",
            await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("CAPA").ConfigureAwait(false);
        Assert.AreEqual("+OK Capability list follows", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        while (!string.Equals(await connection.ReadLineAsync().ConfigureAwait(false), ".", StringComparison.Ordinal))
        {
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapGatewayPresentationJournalsWireBytesAndFailsClosed()
    {
        var port = ReservePort();
        var journal = new RecordingSmtpJournal();
        {
            var server = (await ServerFixture.StartImapAsync(
                         CreateEnvironment(imapPort: port), port, journal: journal).ConfigureAwait(false));
            await using var serverLifetime = server.ConfigureAwait(false);
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
            await using var connectionLifetime = connection.ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* OK ", StringComparison.Ordinal));
            await connection.WriteLineAsync("a1 CAPABILITY").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* CAPABILITY ", StringComparison.Ordinal));
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK ", StringComparison.Ordinal));
            await connection.WriteLineAsync("a2 LOGOUT").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* BYE ", StringComparison.Ordinal));
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK ", StringComparison.Ordinal));
        }

        var sessionId = journal.Records.Single(record => string.Equals(record.Direction, GatewayTrafficDirections.Inbound
, StringComparison.Ordinal) && Encoding.Latin1.GetString(record.Payload).Contains("a1 CAPABILITY\r\n", StringComparison.Ordinal))
            .SessionId;
        var records = journal.Records.Where(record => record.SessionId == sessionId).ToArray();
        Assert.IsTrue(records.Any(record => string.Equals(record.Direction, GatewayTrafficDirections.Outbound
, StringComparison.Ordinal) && Encoding.Latin1.GetString(record.Payload).Contains("* OK ", StringComparison.Ordinal)));
        Assert.IsTrue(records.Any(record => string.Equals(record.Direction, GatewayTrafficDirections.Outbound
, StringComparison.Ordinal) && Encoding.Latin1.GetString(record.Payload).Contains("* CAPABILITY ", StringComparison.Ordinal)));
        Assert.IsTrue(records.All(record => string.Equals(record.Protocol, "imap", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, records.Length).Select(value => (long)value).ToArray(),
            records.Select(record => record.Sequence).ToArray());

        var rejectedPort = ReservePort();
        var rejectedServer = (await ServerFixture.StartImapAsync(
            CreateEnvironment(imapPort: rejectedPort),
            rejectedPort,
            journal: new RecordingSmtpJournal { RejectWrites = true }).ConfigureAwait(false));
        await using var rejectedServerLifetime = rejectedServer.ConfigureAwait(false);
        var rejectedConnection = (await ProtocolConnection.ConnectAsync(rejectedPort).ConfigureAwait(false));
        await using var rejectedConnectionLifetime = rejectedConnection.ConfigureAwait(false);
        await Assert.ThrowsAsync<EndOfStreamException>(() => rejectedConnection.ReadLineAsync()).ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapKeepsConnectionAliveWhenAuthenticationWorkerIsUnavailable()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartImapAsync(
            CreateEnvironment(imapPort: port),
            port,
            applicationService: new UnavailableImapApplicationService()).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.AreEqual(
            "a2 NO [UNAVAILABLE] Authentication service unavailable",
            await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a3 CAPABILITY").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* CAPABILITY ", StringComparison.Ordinal));
        Assert.AreEqual("a3 OK CAPABILITY completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapKeepsConnectionAliveWhenMailboxListingWorkerIsUnavailable()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartImapAsync(
            CreateEnvironment(imapPort: port),
            port,
            applicationService: new UnavailableImapMailboxApplicationService()).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.AreEqual("a2 OK LOGIN completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a3 LIST \"\" \"*\"").ConfigureAwait(false);
        Assert.AreEqual("a3 NO [UNAVAILABLE] Mailboxes unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a4 CAPABILITY").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* CAPABILITY ", StringComparison.Ordinal));
        Assert.AreEqual("a4 OK CAPABILITY completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapKeepsConnectionAliveWhenMailboxStatusWorkerIsUnavailable()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartImapAsync(
            CreateEnvironment(imapPort: port),
            port,
            applicationService: new UnavailableImapMailboxApplicationService(listingUnavailable: false)).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.AreEqual("a2 OK LOGIN completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a3 STATUS INBOX (MESSAGES UIDNEXT SIZE)").ConfigureAwait(false);
        Assert.AreEqual("a3 NO [UNAVAILABLE] Mailbox status unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a4 LIST \"\" \"*\" RETURN (STATUS (MESSAGES))").ConfigureAwait(false);
        Assert.AreEqual("a4 NO [UNAVAILABLE] Mailbox status unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a5 SUBSCRIBE INBOX").ConfigureAwait(false);
        Assert.AreEqual("a5 NO [UNAVAILABLE] Mailbox subscription unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a6 CREATE Projects").ConfigureAwait(false);
        Assert.AreEqual("a6 NO [UNAVAILABLE] Mailbox creation unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a7 RENAME Projects Archive").ConfigureAwait(false);
        Assert.AreEqual("a7 NO [UNAVAILABLE] Mailbox rename unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a8 DELETE Projects").ConfigureAwait(false);
        Assert.AreEqual("a8 NO [UNAVAILABLE] Mailbox deletion unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a9 SELECT INBOX").ConfigureAwait(false);
        Assert.AreEqual("a9 NO [UNAVAILABLE] Mailbox selection unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a10 GETQUOTAROOT INBOX").ConfigureAwait(false);
        Assert.AreEqual("a10 NO [UNAVAILABLE] Quota unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a11 GETQUOTA \"\"").ConfigureAwait(false);
        Assert.AreEqual("a11 NO [UNAVAILABLE] Quota unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a12 CAPABILITY").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* CAPABILITY ", StringComparison.Ordinal));
        Assert.AreEqual("a12 OK CAPABILITY completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapRejectsMalformedSelectionBeforeSendingAnyMailboxLines()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartImapAsync(
            CreateEnvironment(imapPort: port),
            port,
            applicationService: new UnavailableImapMailboxApplicationService(
                listingUnavailable: false, selectionMalformed: true)).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.AreEqual("a2 OK LOGIN completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
        Assert.AreEqual("a3 NO [UNAVAILABLE] Mailbox selection unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a4 CAPABILITY").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* CAPABILITY ", StringComparison.Ordinal));
        Assert.AreEqual("a4 OK CAPABILITY completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapRejectsIdleBeforeContinuationWhenSnapshotWorkerIsUnavailable()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartImapAsync(
            CreateEnvironment(imapPort: port),
            port,
            applicationService: new UnavailableImapMailboxApplicationService(
                listingUnavailable: false, selectionAvailable: true)).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.AreEqual("a2 OK LOGIN completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
        var selected = await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);
        Assert.IsTrue(selected[^1].StartsWith("a3 OK [READ-WRITE]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 IDLE").ConfigureAwait(false);
        Assert.AreEqual("a4 NO [UNAVAILABLE] IDLE backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a5 CAPABILITY").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* CAPABILITY ", StringComparison.Ordinal));
        Assert.AreEqual("a5 OK CAPABILITY completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapExpungeAndCloseKeepSelectionWhenWorkerIsUnavailable()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartImapAsync(
            CreateEnvironment(imapPort: port),
            port,
            applicationService: new UnavailableImapMailboxApplicationService(
                listingUnavailable: false, selectionAvailable: true)).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.AreEqual("a2 OK LOGIN completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
        var selected = await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);
        Assert.IsTrue(selected[^1].StartsWith("a3 OK [READ-WRITE]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 EXPUNGE").ConfigureAwait(false);
        Assert.AreEqual("a4 NO [UNAVAILABLE] EXPUNGE backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a5 STORE 1:* +FLAGS.SILENT (\\Seen)").ConfigureAwait(false);
        Assert.AreEqual("a5 NO [UNAVAILABLE] STORE backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a6 UID STORE $ +FLAGS.SILENT (\\Seen)").ConfigureAwait(false);
        Assert.AreEqual("a6 NO [UNAVAILABLE] UID STORE backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a7 UID EXPUNGE *").ConfigureAwait(false);
        Assert.AreEqual("a7 NO [UNAVAILABLE] UID EXPUNGE backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a8 CLOSE").ConfigureAwait(false);
        Assert.AreEqual("a8 NO [UNAVAILABLE] CLOSE backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a9 MOVE 1 Archive").ConfigureAwait(false);
        Assert.AreEqual("a9 NO [UNAVAILABLE] MOVE backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a10 UID MOVE $ Archive").ConfigureAwait(false);
        Assert.AreEqual("a10 NO [UNAVAILABLE] UID MOVE backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a11 COPY 1 Archive").ConfigureAwait(false);
        Assert.AreEqual("a11 NO [UNAVAILABLE] COPY backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a12 UID COPY $ Archive").ConfigureAwait(false);
        Assert.AreEqual("a12 NO [UNAVAILABLE] UID COPY backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a13 UNSELECT").ConfigureAwait(false);
        Assert.AreEqual("a13 OK UNSELECT completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapMutationsRejectMalformedWorkerResultsBeforeOutput()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartImapAsync(
            CreateEnvironment(imapPort: port),
            port,
            applicationService: new UnavailableImapMailboxApplicationService(
                listingUnavailable: false, selectionAvailable: true,
                storeMalformed: true, moveMalformed: true, copyMalformed: true)).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.AreEqual("a2 OK LOGIN completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
        var selected = await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);
        Assert.IsTrue(selected[^1].StartsWith("a3 OK [READ-WRITE]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 STORE 1 +FLAGS (\\Seen)").ConfigureAwait(false);
        Assert.AreEqual("a4 NO [UNAVAILABLE] STORE backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a5 MOVE 1 Archive").ConfigureAwait(false);
        Assert.AreEqual("a5 NO [UNAVAILABLE] MOVE backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a6 COPY 1 Archive").ConfigureAwait(false);
        Assert.AreEqual("a6 NO [UNAVAILABLE] COPY backend unavailable", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a7 UNSELECT").ConfigureAwait(false);
        Assert.AreEqual("a7 OK UNSELECT completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
    }

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
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("220 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        var capability = await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
        StringAssert.Contains(capability, "250-STARTTLS", StringComparison.Ordinal);
        Assert.IsFalse(capability.Contains("AUTH", StringComparison.Ordinal));
        StringAssert.Contains(capability, "250-8BITMIME", StringComparison.Ordinal);
        StringAssert.Contains(capability, "250-SMTPUTF8", StringComparison.Ordinal);
        StringAssert.Contains(capability, "250-DSN", StringComparison.Ordinal);

        await connection.WriteLineAsync("AUTH PLAIN AGZvbwBiYXI=").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("538 ", StringComparison.Ordinal));

        await connection.WriteLineAsync(new string('X', 5000)).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("500 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRefreshesConnectionTimeoutAfterClientActivity()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port, connectionTimeoutSeconds: 2);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("220 ", StringComparison.Ordinal));
        for (var index = 0; index < 5; index++)
        {
            await Task.Delay(550).ConfigureAwait(false);
            await connection.WriteLineAsync("NOOP").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpStartTlsAdvertisesAuthenticationOnlyAfterUpgrade()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        Assert.IsFalse((await connection.ReadSmtpResponseAsync().ConfigureAwait(false)).Contains("AUTH", StringComparison.Ordinal));

        await connection.WriteLineAsync("STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("220 ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);

        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        var capability = await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
        StringAssert.Contains(capability, "250-AUTH PLAIN LOGIN", StringComparison.Ordinal);
        Assert.IsFalse(capability.Contains("STARTTLS", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ThunderbirdXOAuth2AuthenticatesSmtpImapAndPop3 scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
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
        var smtpServer = (await ServerFixture.StartSmtpAsync(environment, smtpPort).ConfigureAwait(false));
        await using var smtpServerLifetime = smtpServer.ConfigureAwait(false);
        var imapServer = (await ServerFixture.StartImapAsync(environment, imapPort).ConfigureAwait(false));
        await using var imapServerLifetime = imapServer.ConfigureAwait(false);
        var pop3Server = (await ServerFixture.StartPop3Async(environment, pop3Port).ConfigureAwait(false));
        await using var pop3ServerLifetime = pop3Server.ConfigureAwait(false);

        var smtpToken = await smtpServer.CreateOAuthAccessTokenAsync("smtp").ConfigureAwait(false);
        {
            var smtp = (await ProtocolConnection.ConnectAsync(smtpPort).ConfigureAwait(false));
            await using var smtpLifetime = smtp.ConfigureAwait(false);
            await smtp.ReadLineAsync().ConfigureAwait(false);
            await smtp.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
            await smtp.ReadSmtpResponseAsync().ConfigureAwait(false);
            await smtp.WriteLineAsync("STARTTLS").ConfigureAwait(false);
            Assert.IsTrue((await smtp.ReadLineAsync().ConfigureAwait(false)).StartsWith("220 ", StringComparison.Ordinal));
            await smtp.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            await smtp.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
            StringAssert.Contains(await smtp.ReadSmtpResponseAsync().ConfigureAwait(false), "XOAUTH2", StringComparison.Ordinal);
            await smtp.WriteLineAsync($"AUTH XOAUTH2 {CreateXOAuth2Response(smtpToken)}").ConfigureAwait(false);
            Assert.IsTrue((await smtp.ReadLineAsync().ConfigureAwait(false)).StartsWith("235 ", StringComparison.Ordinal));
        }

        var imapToken = await imapServer.CreateOAuthAccessTokenAsync("imap").ConfigureAwait(false);
        {
            var imap = (await ProtocolConnection.ConnectAsync(imapPort).ConfigureAwait(false));
            await using var imapLifetime = imap.ConfigureAwait(false);
            await imap.ReadLineAsync().ConfigureAwait(false);
            await imap.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
            Assert.IsTrue((await imap.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
            await imap.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            await imap.WriteLineAsync("a2 CAPABILITY").ConfigureAwait(false);
            StringAssert.Contains(await imap.ReadLineAsync().ConfigureAwait(false), "AUTH=XOAUTH2", StringComparison.Ordinal);
            Assert.IsTrue((await imap.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
            await imap.WriteLineAsync(
                $"a3 AUTHENTICATE XOAUTH2 {CreateXOAuth2Response(imapToken)}").ConfigureAwait(false);
            Assert.IsTrue((await imap.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK", StringComparison.Ordinal));
        }

        var pop3Token = await pop3Server.CreateOAuthAccessTokenAsync("pop").ConfigureAwait(false);
        {
            var pop3 = (await ProtocolConnection.ConnectAsync(pop3Port).ConfigureAwait(false));
            await using var pop3Lifetime = pop3.ConfigureAwait(false);
            await pop3.ReadLineAsync().ConfigureAwait(false);
            await pop3.WriteLineAsync("STLS").ConfigureAwait(false);
            Assert.IsTrue((await pop3.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
            await pop3.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            await pop3.WriteLineAsync("CAPA").ConfigureAwait(false);
            CollectionAssert.Contains(
                await ReadPop3MultilineAsync(pop3).ConfigureAwait(false),
                "SASL PLAIN XOAUTH2");
            await pop3.WriteLineAsync("AUTH XOAUTH2").ConfigureAwait(false);
            Assert.AreEqual("+ ", await pop3.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
            await pop3.WriteLineAsync(CreateXOAuth2Response(pop3Token)).ConfigureAwait(false);
            Assert.IsTrue((await pop3.ReadLineAsync().ConfigureAwait(false)).StartsWith(
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
        var smtpServer = (await ServerFixture.StartSmtpAsync(
            environment,
            smtpPort,
            smtpLogger).ConfigureAwait(false));
        await using var smtpServerLifetime = smtpServer.ConfigureAwait(false);
        var imapServer = (await ServerFixture.StartImapAsync(
            environment,
            imapPort,
            imapLogger).ConfigureAwait(false));
        await using var imapServerLifetime = imapServer.ConfigureAwait(false);

        await TriggerTlsPeerFailureAsync(smtpPort, sendMalformedPayload: false).ConfigureAwait(false);
        await TriggerTlsPeerFailureAsync(smtpPort, sendMalformedPayload: true).ConfigureAwait(false);
        await TriggerTlsPeerFailureAsync(imapPort, sendMalformedPayload: false).ConfigureAwait(false);
        await TriggerTlsPeerFailureAsync(imapPort, sendMalformedPayload: true).ConfigureAwait(false);

        await WaitForAsync(
            () => CountTlsHandshakeDebugLogs(smtpLogger) >= 2,
            "SMTP did not record both TLS peer failures at debug level.").ConfigureAwait(false);
        await WaitForAsync(
            () => CountTlsHandshakeDebugLogs(imapLogger) >= 2,
            "IMAP did not record both TLS peer failures at debug level.").ConfigureAwait(false);

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
        var smtpServer = (await ServerFixture.StartSmtpAsync(
            environment,
            smtpPort,
            smtpLogger).ConfigureAwait(false));
        await using var smtpServerLifetime = smtpServer.ConfigureAwait(false);
        var imapServer = (await ServerFixture.StartImapAsync(
            environment,
            imapPort,
            imapLogger).ConfigureAwait(false));
        await using var imapServerLifetime = imapServer.ConfigureAwait(false);

        await WaitForAsync(
            () => HasCertificateLoadWarning(smtpLogger),
            "SMTP did not record the certificate load failure as a warning.").ConfigureAwait(false);
        await WaitForAsync(
            () => HasCertificateLoadWarning(imapLogger),
            "IMAP did not record the certificate load failure as a warning.").ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpSubmissionRequiresTlsBeforeMail()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(submissionPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("MAIL FROM:<sender@tenant.example.test>").ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("530 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsMessageWhenBufferedDataReachesItsLimit()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@tenant.example.test>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("354 ", StringComparison.Ordinal));

        var dataLine = new string('a', 900);
        for (var index = 0; index < 80; index++)
            await connection.WriteLineAsync(dataLine).ConfigureAwait(false);
        await connection.WriteLineAsync(".").ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("552 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);

        await connection.WriteLineAsync("NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task SmtpBoundsConcurrentDataBuffers()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connections = new List<ProtocolConnection>();

        try
        {
            for (var index = 0; index < 5; index++)
            {
                var connection = await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false);
                connections.Add(connection);
                await connection.ReadLineAsync().ConfigureAwait(false);
                await BeginInboundEnvelopeAsync(connection).ConfigureAwait(false);
                await connection.WriteLineAsync("DATA").ConfigureAwait(false);

                var response = await connection.ReadLineAsync().ConfigureAwait(false);
                if (index < 4)
                    Assert.IsTrue(response.StartsWith("354 ", StringComparison.Ordinal));
                else
                    Assert.IsTrue(response.StartsWith("452 4.3.2", StringComparison.Ordinal));
            }

            await connections[0].WriteLineAsync(".").ConfigureAwait(false);
            Assert.IsTrue((await connections[0].ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));

            await connections[4].WriteLineAsync("DATA").ConfigureAwait(false);
            Assert.IsTrue((await connections[4].ReadLineAsync().ConfigureAwait(false)).StartsWith("354 ", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var connection in connections)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsLongDataAndPreservesEightBitData()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await BeginInboundMessageAsync(connection).ConfigureAwait(false);
        await connection.WriteLineAsync(new string('a', 999)).ConfigureAwait(false);
        await connection.WriteLineAsync(".").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("554 5.6.0", StringComparison.Ordinal));

        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> BODY=8BITMIME").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@tenant.example.test>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("354 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("Subject: eight-bit body").ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await connection.WriteLineAsync("body café").ConfigureAwait(false);
        await connection.WriteLineAsync(".").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
        StringAssert.Contains(server.MailQueue.LastSubmission!.RawMessage, "café", StringComparison.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpAcceptsInternationalizedEnvelopeAndHeadersWithSmtpUtf8()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadSmtpResponseAsync().ConfigureAwait(false), "250-SMTPUTF8", StringComparison.Ordinal);
        await connection.WriteUtf8LineAsync(
            "MAIL FROM:<josé@example.com> BODY=8BITMIME SMTPUTF8").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync(
            @"RCPT TO:<δοκιμή@tenant.example.test> ORCPT=utf-8;\x{3B4}\x{3BF}\x{3BA}\x{3B9}\x{3BC}\x{3AE}@tenant.example.test").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("354 ", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("From: José <josé@example.com>").ConfigureAwait(false);
        await connection.WriteUtf8LineAsync("To: δοκιμή@tenant.example.test").ConfigureAwait(false);
        await connection.WriteUtf8LineAsync("Subject: Žuta pošta").ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await connection.WriteUtf8LineAsync("Pozdrav iz Zagreba").ConfigureAwait(false);
        await connection.WriteLineAsync(".").ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        var submission = server.MailQueue.LastSubmission!;
        Assert.IsTrue(submission.RequiresSmtpUtf8);
        Assert.AreEqual("josé@example.com", submission.EnvelopeSender, StringComparer.Ordinal);
        var recipient = submission.Recipients.Single();
        Assert.AreEqual("δοκιμή@tenant.example.test", recipient.Address, StringComparer.Ordinal);
        Assert.AreEqual(
            @"utf-8;\x{3B4}\x{3BF}\x{3BA}\x{3B9}\x{3BC}\x{3AE}@tenant.example.test",
            recipient.Dsn!.OriginalRecipient, StringComparer.Ordinal);
        var decoded = Encoding.UTF8.GetString(
            Encoding.Latin1.GetBytes(submission.RawMessage));
        StringAssert.Contains(decoded, "with UTF8SMTP", StringComparison.Ordinal);
        StringAssert.Contains(decoded, "Subject: Žuta pošta", StringComparison.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpAcceptsAndQueuesDeliveryStatusParameters()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadSmtpResponseAsync().ConfigureAwait(false), "250-DSN", StringComparison.Ordinal);
        await connection.WriteLineAsync(
            "MAIL FROM:<sender@example.com> RET=hdrs ENVID=job+2B42+3Ddone").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync(
            "RCPT TO:<postmaster@tenant.example.test> " +
            "NOTIFY=success,failure,delay " +
            "ORCPT=rfc822;old+2Btag+40example.com").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("354 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("Subject: DSN request").ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await connection.WriteLineAsync("body").ConfigureAwait(false);
        await connection.WriteLineAsync(".").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));

        var submission = server.MailQueue.LastSubmission!;
        Assert.AreEqual("HDRS", submission.Dsn!.ReturnContent, StringComparer.Ordinal);
        Assert.AreEqual("job+2B42+3Ddone", submission.Dsn.EnvelopeId, StringComparer.Ordinal);
        var recipient = submission.Recipients.Single();
        Assert.AreEqual("SUCCESS,FAILURE,DELAY", recipient.Dsn!.Notify, StringComparer.Ordinal);
        Assert.AreEqual("rfc822;old+2Btag+40example.com", recipient.Dsn.OriginalRecipient, StringComparer.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsMalformedOrMisplacedDeliveryStatusParameters()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("HELO client.example").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> RET=HDRS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("555 5.5.4", StringComparison.Ordinal));

        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
        await connection.WriteLineAsync(
            "MAIL FROM:<sender@example.com> RET=HDRS RET=FULL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> ENVID=job+2b42").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "RCPT TO:<postmaster@tenant.example.test> NOTIFY=NEVER,FAILURE").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync(
            "RCPT TO:<postmaster@tenant.example.test> " +
            "ORCPT=rfc822;one@example.com ORCPT=rfc822;two@example.com").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync(
            "RCPT TO:<postmaster@tenant.example.test> ORCPT=utf-8;δοκιμή@example.com").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync(
            @"RCPT TO:<postmaster@tenant.example.test> ORCPT=utf-8;\x{3B4}\x{3BF}\x{3BA}\x{3B9}\x{3BC}\x{3AE}@example.com").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@tenant.example.test> FUTURE=value").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("555 5.5.4", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsUndeclaredInternationalizedContentAndInvalidParameters()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);

        await connection.WriteUtf8LineAsync("MAIL FROM:<josé@example.com> BODY=8BITMIME").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("550 5.6.7", StringComparison.Ordinal));

        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> BODY=8BITMIME").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("RCPT TO:<δοκιμή@tenant.example.test>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("553 5.6.7", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@tenant.example.test>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("354 ", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("Subject: Žuta pošta").ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await connection.WriteLineAsync("body").ConfigureAwait(false);
        await connection.WriteLineAsync(".").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("554 5.6.9", StringComparison.Ordinal));

        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> FUTURE=value").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("555 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> SMTPUTF8=value").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("501 5.5.4", StringComparison.Ordinal));
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com> SIZE=999999999").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("552 5.3.4", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsInvalidUtf8CommandOctets()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteBytesAsync(
            Encoding.ASCII.GetBytes("EHLO client.")
                .Concat(new byte[] { 0xc3, 0x28 })
                .Concat("\r\n"u8.ToArray())
                .ToArray()).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("500 5.5.2", StringComparison.Ordinal));

        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadSmtpResponseAsync().ConfigureAwait(false), "250-SMTPUTF8", StringComparison.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpQueuesBoundedMessageAndResetsTransaction()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com>").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("RCPT TO:<postmaster@tenant.example.test>").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("Subject: bounded").ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await connection.WriteLineAsync("message body").ConfigureAwait(false);
        await connection.WriteLineAsync(".").ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
        Assert.IsNotNull(server.MailQueue.LastSubmission);
        StringAssert.StartsWith(server.MailQueue.LastSubmission.RawMessage, "Received: from client.example", StringComparison.Ordinal);
        Assert.AreEqual("sender@example.com", server.MailQueue.LastSubmission.EnvelopeSender, StringComparer.Ordinal);
        Assert.AreEqual("postmaster@tenant.example.test", server.MailQueue.LastSubmission.Recipients.Single().Address, StringComparer.Ordinal);
        Assert.IsTrue(server.MailQueue.LastSubmission.Recipients.Single().IsLocal);

        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("503 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpReturnsTemporaryFailureWhenQueuePersistenceFails()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        server.MailQueue.ThrowOnEnqueue = true;
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await AuthenticateSmtpAsync(connection).ConfigureAwait(false);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync($"From: {TestUsername}").ConfigureAwait(false);
        await connection.WriteLineAsync("To: recipient@example.com").ConfigureAwait(false);
        await connection.WriteLineAsync("Subject: signing failure").ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await connection.WriteLineAsync("body").ConfigureAwait(false);
        await connection.WriteLineAsync(".").ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("451 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);

        await connection.WriteLineAsync("NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpAcceptsOwnedSenderWithMatchingFromHeader()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await AuthenticateSmtpAsync(connection).ConfigureAwait(false);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("354 ", StringComparison.Ordinal));
        await connection.WriteLineAsync($"From: Test User <{TestUsername}>").ConfigureAwait(false);
        await connection.WriteLineAsync("To: recipient@example.com").ConfigureAwait(false);
        await connection.WriteLineAsync("Subject: authorized sender").ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await connection.WriteLineAsync("body").ConfigureAwait(false);
        await connection.WriteLineAsync(".").ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsUnownedEnvelopeSender()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await AuthenticateSmtpAsync(connection).ConfigureAwait(false);
        await connection.WriteLineAsync("MAIL FROM:<other@tenant.example.test>").ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("553 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("503 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsMismatchedFromHeaderBeforeSigning()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await AuthenticateSmtpAsync(connection).ConfigureAwait(false);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("From: other@tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync("To: recipient@example.com").ConfigureAwait(false);
        await connection.WriteLineAsync("Subject: rejected sender").ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await connection.WriteLineAsync("body").ConfigureAwait(false);
        await connection.WriteLineAsync(".").ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("550 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("503 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsAuthenticationDuringMailTransaction()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await UpgradeSmtpToTlsAsync(connection).ConfigureAwait(false);
        await connection.WriteLineAsync("MAIL FROM:<other@tenant.example.test>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
        await connection.WriteLineAsync($"AUTH PLAIN {credentials}").ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("503 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RSET").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync($"AUTH PLAIN {credentials}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("235 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRechecksSenderOwnershipBeforeDelivery()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        var server = (await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await AuthenticateSmtpAsync(connection).ConfigureAwait(false);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await server.DisableOwnedAddressAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync($"From: {TestUsername}").ConfigureAwait(false);
        await connection.WriteLineAsync("To: recipient@example.com").ConfigureAwait(false);
        await connection.WriteLineAsync("Subject: disabled sender").ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await connection.WriteLineAsync("body").ConfigureAwait(false);
        await connection.WriteLineAsync(".").ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("550 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapClearTextDisablesLoginAndHasNoByteOrderMark()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a1 CAPABILITY").ConfigureAwait(false);
        var capability = await connection.ReadLineAsync().ConfigureAwait(false);
        StringAssert.Contains(capability, "IMAP4rev1 LITERAL+ IDLE NAMESPACE SPECIAL-USE UIDPLUS", StringComparison.Ordinal);
        StringAssert.Contains(capability, "LIST-EXTENDED LIST-STATUS", StringComparison.Ordinal);
        StringAssert.Contains(capability, "ID ENABLE MOVE UNSELECT QUOTA CONDSTORE QRESYNC ESEARCH", StringComparison.Ordinal);
        StringAssert.Contains(capability, "SEARCHRES UTF8=ACCEPT", StringComparison.Ordinal);
        StringAssert.Contains(
            capability,
            "SORT THREAD=ORDEREDSUBJECT THREAD=REFERENCES BINARY OBJECTID MULTIAPPEND STATUS=SIZE " +
            "COMPRESS=DEFLATE APPENDLIMIT=65536", StringComparison.Ordinal);
        StringAssert.Contains(capability, "LOGINDISABLED", StringComparison.Ordinal);
        StringAssert.Contains(capability, "STARTTLS", StringComparison.Ordinal);
        Assert.IsFalse(capability.Contains("AUTH=PLAIN", StringComparison.Ordinal));
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a2 LOGIN user password").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "[PRIVACYREQUIRED]", StringComparison.Ordinal);

        await connection.WriteLineAsync("a3 LOGIN {4}").ConfigureAwait(false);
        var literalLoginResponse = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.IsTrue(literalLoginResponse.StartsWith("a3 NO", StringComparison.Ordinal));
        StringAssert.Contains(literalLoginResponse, "[PRIVACYREQUIRED]", StringComparison.Ordinal);
        await connection.WriteLineAsync("a4 NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 LOGIN {4+}").ConfigureAwait(false);
        literalLoginResponse = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.IsTrue(literalLoginResponse.StartsWith("* BYE", StringComparison.Ordinal));
        StringAssert.Contains(literalLoginResponse, "[PRIVACYREQUIRED]", StringComparison.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapRefreshesConnectionTimeoutAfterClientActivity()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port, connectionTimeoutSeconds: 2);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* OK", StringComparison.Ordinal));
        for (var index = 0; index < 5; index++)
        {
            await Task.Delay(550).ConfigureAwait(false);
            var tag = $"a{index}";
            await connection.WriteLineAsync($"{tag} NOOP").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith($"{tag} OK", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapStartTlsAdvertisesAuthenticationAfterUpgrade()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);

        await connection.WriteLineAsync("a2 CAPABILITY").ConfigureAwait(false);
        var capability = await connection.ReadLineAsync().ConfigureAwait(false);
        StringAssert.Contains(capability, "AUTH=PLAIN", StringComparison.Ordinal);
        StringAssert.Contains(capability, "SASL-IR", StringComparison.Ordinal);
        Assert.IsFalse(capability.Contains("LOGINDISABLED", StringComparison.Ordinal));
        Assert.IsFalse(capability.Contains("STARTTLS", StringComparison.Ordinal));
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAuthenticatePlainSupportsInitialResponseAndRejectsProxyAuthorization()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);

        {
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
            await using var connectionLifetime = connection.ConfigureAwait(false);
            await connection.ReadLineAsync().ConfigureAwait(false);
            await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
            await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);

            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
            await connection.WriteLineAsync($"a2 AUTHENTICATE PLAIN {credentials}").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync("a3 NOOP").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK", StringComparison.Ordinal));
        }

        var rejected = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var rejectedLifetime = rejected.ConfigureAwait(false);
        await rejected.ReadLineAsync().ConfigureAwait(false);
        await rejected.WriteLineAsync("b1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await rejected.ReadLineAsync().ConfigureAwait(false)).StartsWith("b1 OK", StringComparison.Ordinal));
        await rejected.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);

        var proxyCredentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"other@tenant.example.test\0{TestUsername}\0{TestPassword}"));
        await rejected.WriteLineAsync($"b2 AUTHENTICATE PLAIN {proxyCredentials}").ConfigureAwait(false);
        Assert.IsTrue((await rejected.ReadLineAsync().ConfigureAwait(false)).StartsWith("b2 NO", StringComparison.Ordinal));
        await rejected.WriteLineAsync($"b3 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await rejected.ReadLineAsync().ConfigureAwait(false)).StartsWith("b3 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapEnableRequiresAuthenticatedState()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 ENABLE QRESYNC").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a2 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a3 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 ENABLE QRESYNC CONDSTORE UTF8=ACCEPT UNKNOWN").ConfigureAwait(false);
        Assert.AreEqual("* ENABLED QRESYNC CONDSTORE UTF8=ACCEPT", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5 SELECT INBOX").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a5").ConfigureAwait(false);
        await connection.WriteLineAsync("a6 ENABLE QRESYNC").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 BAD", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapRejectsUnknownCompressionWithoutChangingTheTransport()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 COMPRESS GZIP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapCompressesCommandsAndResponsesAfterNegotiation()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 COMPRESS DEFLATE").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK", StringComparison.Ordinal));
        await connection.UpgradeToDeflateAsync().ConfigureAwait(false);

        await connection.WriteLineAsync("a4 NOOP").ConfigureAwait(false);
        Assert.AreEqual("a4 OK NOOP completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a5 COMPRESS DEFLATE").ConfigureAwait(false);
        Assert.AreEqual("a5 BAD COMPRESS already active", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("a6 LOGOUT").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("* BYE", StringComparison.Ordinal));
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The Pop3StartTlsSupportsThunderbirdRetrievalAndTransactionalDeletion scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task Pop3StartTlsSupportsThunderbirdRetrievalAndTransactionalDeletion()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(pop3Port: port);
        var server = (await ServerFixture.StartPop3Async(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedPop3MessagesAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("CAPA").ConfigureAwait(false);
        var clearCapabilities = await ReadPop3MultilineAsync(connection).ConfigureAwait(false);
        CollectionAssert.Contains(clearCapabilities, "STLS");
        CollectionAssert.Contains(clearCapabilities, "UIDL");
        CollectionAssert.Contains(clearCapabilities, "TOP");
        Assert.IsFalse(clearCapabilities.Any(line => line.StartsWith("SASL", StringComparison.Ordinal)));

        await connection.WriteLineAsync($"USER {TestUsername}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("-ERR [AUTH]", StringComparison.Ordinal));
        await connection.WriteLineAsync("STLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);

        await connection.WriteLineAsync("CAPA").ConfigureAwait(false);
        var secureCapabilities = await ReadPop3MultilineAsync(connection).ConfigureAwait(false);
        CollectionAssert.Contains(secureCapabilities, "SASL PLAIN");
        Assert.IsFalse(secureCapabilities.Contains("STLS"));

        await connection.WriteLineAsync($"USER {TestUsername}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync($"PASS {TestPassword}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK maildrop has 2 messages", StringComparison.Ordinal));

        await connection.WriteLineAsync("UIDL").ConfigureAwait(false);
        var uidls = await ReadPop3MultilineAsync(connection).ConfigureAwait(false);
        Assert.HasCount(3, uidls);
        Assert.IsTrue(uidls[1].StartsWith("1 E", StringComparison.Ordinal));
        Assert.IsTrue(uidls[2].StartsWith("2 E", StringComparison.Ordinal));
        Assert.AreNotEqual(uidls[1].Split(' ')[1], uidls[2].Split(' ')[1], StringComparer.Ordinal);

        await connection.WriteLineAsync("TOP 1 1").ConfigureAwait(false);
        var top = await ReadPop3MultilineAsync(connection).ConfigureAwait(false);
        Assert.IsTrue(top[0].StartsWith("+OK ", StringComparison.Ordinal));
        CollectionAssert.Contains(top, "Subject: POP first");
        CollectionAssert.Contains(top, "..leading dot");
        Assert.IsFalse(top.Contains("second body line"));

        await connection.WriteLineAsync("RETR 1").ConfigureAwait(false);
        var retrieved = await ReadPop3MultilineAsync(connection).ConfigureAwait(false);
        Assert.IsTrue(retrieved[0].StartsWith("+OK ", StringComparison.Ordinal));
        CollectionAssert.Contains(retrieved, "..leading dot");
        CollectionAssert.Contains(retrieved, "second body line");

        await connection.WriteLineAsync("DELE 1").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("STAT").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK 1 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RSET").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK 2 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DELE 1").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("QUIT").ConfigureAwait(false);
        Assert.AreEqual("+OK goodbye (1 messages deleted)", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);

        Assert.AreEqual(1, await server.CountStoredEmailsAsync().ConfigureAwait(false));
        Assert.AreEqual(1, await server.CountExpungedUidsAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task Pop3ImplicitTlsSupportsSaslPlainAndRollsBackAnAbandonedDelete()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(pop3ImplicitTlsPort: port);
        var server = (await ServerFixture.StartPop3Async(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedPop3MessagesAsync().ConfigureAwait(false);

        {
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
            await using var connectionLifetime = connection.ConfigureAwait(false);
            await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
            await connection.WriteLineAsync($"AUTH PLAIN {credentials}").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK maildrop has 2 messages", StringComparison.Ordinal));
            await connection.WriteLineAsync("DELE 2").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
        }

        Assert.AreEqual(2, await server.CountStoredEmailsAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await server.CountExpungedUidsAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapReportsNamespaceIdentityAndQuotaForThunderbirdDiscovery()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedInboxMessagesForSearchAsync().ConfigureAwait(false);
        await server.SetUserQuotaAsync(4096).ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 ID (\"name\" \"Thunderbird\")").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "\"name\" \"mk8.email\"", StringComparison.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 NAMESPACE").ConfigureAwait(false);
        Assert.AreEqual("* NAMESPACE ((\"\" \"/\")) NIL NIL", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 GETQUOTAROOT INBOX").ConfigureAwait(false);
        Assert.AreEqual("* QUOTAROOT \"INBOX\" \"\"", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.AreEqual("* QUOTA \"\" (STORAGE 1 4)", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 GETQUOTA \"\"").ConfigureAwait(false);
        Assert.AreEqual("* QUOTA \"\" (STORAGE 1 4)", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 GETQUOTA missing").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "[NONEXISTENT]", StringComparison.Ordinal);
        await connection.WriteLineAsync("a8 GETQUOTAROOT missing").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "[NONEXISTENT]", StringComparison.Ordinal);

        await server.SetUserQuotaAsync(0).ConfigureAwait(false);
        await connection.WriteLineAsync("a9 GETQUOTA \"\"").ConfigureAwait(false);
        Assert.AreEqual("* QUOTA \"\" ()", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a9 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAcceptsBoundedCommandLiterals()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedInboxMessagesForSearchAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);

        await connection.WriteRawAsync(
            $"a2 LOGIN {{{TestUsername.Length}+}}\r\n{TestUsername} {{{TestPassword.Length}+}}\r\n{TestPassword}\r\n").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);

        const string subject = "Third \"quoted\" subject";
        await connection.WriteLineAsync($"a4 SEARCH SUBJECT {{{subject.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(subject).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        var responses = await ReadUntilTaggedResponseAsync(connection, "a4").ConfigureAwait(false);
        Assert.IsTrue(responses.Contains("* SEARCH 3"));

        const string bodyNeedle = "first body\r\n";
        await connection.WriteRawAsync($"a5 SEARCH BODY {{{bodyNeedle.Length}+}}\r\n{bodyNeedle}\r\n").ConfigureAwait(false);
        responses = await ReadUntilTaggedResponseAsync(connection, "a5").ConfigureAwait(false);
        Assert.IsTrue(responses.Contains("* SEARCH 1"));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAcceptsLiteralAppendMailboxName()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        const string message = "From: user@tenant.example.test\r\nTo: user@tenant.example.test\r\nSubject: literal mailbox\r\n\r\nbody\r\n";
        await connection.WriteRawAsync($"a3 APPEND {{4+}}\r\nSent {{{message.Length}}}\r\n").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));
        Assert.AreEqual("literal mailbox", (await server.GetStoredEmailAsync(DefaultFolders.Sent).ConfigureAwait(false)).Subject, StringComparer.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapRejectsOversizedCommandLiteralsWithoutUnboundedReads()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);

        {
            var synchronized = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
            await using var synchronizedLifetime = synchronized.ConfigureAwait(false);
            await synchronized.ReadLineAsync().ConfigureAwait(false);
            await synchronized.WriteLineAsync("a1 ID {16385}").ConfigureAwait(false);
            Assert.IsTrue((await synchronized.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 BAD", StringComparison.Ordinal));
            await synchronized.WriteLineAsync("a2 NOOP").ConfigureAwait(false);
            Assert.IsTrue((await synchronized.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        }

        var nonSynchronizing = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var nonSynchronizingLifetime = nonSynchronizing.ConfigureAwait(false);
        await nonSynchronizing.ReadLineAsync().ConfigureAwait(false);
        await nonSynchronizing.WriteLineAsync("b1 ID {16385+}").ConfigureAwait(false);
        Assert.IsTrue((await nonSynchronizing.ReadLineAsync().ConfigureAwait(false)).StartsWith("* BYE", StringComparison.Ordinal));

        var longLine = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var longLineLifetime = longLine.ConfigureAwait(false);
        await longLine.ReadLineAsync().ConfigureAwait(false);
        await longLine.WriteLineAsync(new string('X', 16_385)).ConfigureAwait(false);
        Assert.IsTrue((await longLine.ReadLineAsync().ConfigureAwait(false)).StartsWith("* BYE", StringComparison.Ordinal));

        var append = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var appendLifetime = append.ConfigureAwait(false);
        await append.ReadLineAsync().ConfigureAwait(false);
        await append.WriteLineAsync("c1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await append.ReadLineAsync().ConfigureAwait(false)).StartsWith("c1 OK", StringComparison.Ordinal));
        await append.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await append.WriteLineAsync($"c2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await append.ReadLineAsync().ConfigureAwait(false)).StartsWith("c2 OK", StringComparison.Ordinal));
        const string message = "From: user@tenant.example.test\r\nTo: user@tenant.example.test\r\nSubject: long continuation\r\n\r\nbody\r\n";
        await append.WriteLineAsync($"c3 APPEND \"Sent\" {{{message.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await append.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await append.WriteRawAsync(message).ConfigureAwait(false);
        await append.WriteLineAsync(new string('X', 16_385)).ConfigureAwait(false);
        Assert.IsTrue((await append.ReadLineAsync().ConfigureAwait(false)).StartsWith("* BYE", StringComparison.Ordinal));
        Assert.AreEqual(0, await server.CountStoredEmailsAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapPrimaryMailboxUsesStandardInboxName()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);

        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 LIST \"\" \"*\"").ConfigureAwait(false);
        var listLines = new List<string>();
        string line;
        do
        {
            line = await connection.ReadLineAsync().ConfigureAwait(false);
            listLines.Add(line);
        }
        while (!line.StartsWith("a3 ", StringComparison.Ordinal));

        Assert.IsTrue(listLines.Any(value => value.Contains("\"INBOX\"", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a4 SELECT INBOX").ConfigureAwait(false);
        do
        {
            line = await connection.ReadLineAsync().ConfigureAwait(false);
        }
        while (!line.StartsWith("a4 ", StringComparison.Ordinal));

        Assert.IsTrue(line.StartsWith("a4 OK [READ-WRITE]", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapManagesPrimaryMailboxHierarchyUsingThunderbirdNames scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapManagesPrimaryMailboxHierarchyUsingThunderbirdNames()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 CREATE Projects").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 CREATE Projects/2026").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5 CREATE Projects/2026").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "[ALREADYEXISTS]", StringComparison.Ordinal);

        await connection.WriteLineAsync("a6 LIST \"\" \"*\"").ConfigureAwait(false);
        var listed = await ReadUntilTaggedResponseAsync(connection, "a6").ConfigureAwait(false);
        Assert.IsTrue(listed.Any(line =>
            line.Contains("(\\HasChildren)", StringComparison.Ordinal)
            && line.EndsWith("\"Projects\"", StringComparison.Ordinal)));
        Assert.IsTrue(listed.Any(line =>
            line.Contains("(\\HasNoChildren)", StringComparison.Ordinal)
            && line.EndsWith("\"Projects/2026\"", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a7 UNSUBSCRIBE Projects").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a8 LSUB \"\" \"*\"").ConfigureAwait(false);
        var subscribed = await ReadUntilTaggedResponseAsync(connection, "a8").ConfigureAwait(false);
        Assert.IsTrue(subscribed.Any(line =>
            line.Contains("(\\Noselect \\HasChildren)", StringComparison.Ordinal)
            && line.EndsWith("\"Projects\"", StringComparison.Ordinal)));
        Assert.IsTrue(subscribed.Any(line => line.EndsWith("\"Projects/2026\"", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a9 SELECT Projects/2026").ConfigureAwait(false);
        var selected = await ReadUntilTaggedResponseAsync(connection, "a9").ConfigureAwait(false);
        Assert.IsTrue(selected[^1].StartsWith("a9 OK [READ-WRITE]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a10 STATUS Projects/2026 (MESSAGES UIDNEXT)").ConfigureAwait(false);
        Assert.AreEqual("* STATUS \"Projects/2026\" (MESSAGES 0 UIDNEXT 1)", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a10 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 RENAME Projects Archive").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a11 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a12 SELECT Projects/2026").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "Mailbox not found", StringComparison.Ordinal);
        await connection.WriteLineAsync("a13 SELECT Archive/2026").ConfigureAwait(false);
        selected = await ReadUntilTaggedResponseAsync(connection, "a13").ConfigureAwait(false);
        Assert.IsTrue(selected[^1].StartsWith("a13 OK [READ-WRITE]", StringComparison.Ordinal));

        await connection.WriteLineAsync("a14 DELETE Archive").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a14 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a15 LIST \"\" \"Archive*\"").ConfigureAwait(false);
        listed = await ReadUntilTaggedResponseAsync(connection, "a15").ConfigureAwait(false);
        Assert.IsTrue(listed.Any(line =>
            line.Contains("(\\Noselect \\HasChildren)", StringComparison.Ordinal)
            && line.EndsWith("\"Archive\"", StringComparison.Ordinal)));
        Assert.IsTrue(listed.Any(line => line.EndsWith("\"Archive/2026\"", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a16 DELETE Archive/2026").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a16 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a17 LIST \"\" \"Archive*\"").ConfigureAwait(false);
        listed = await ReadUntilTaggedResponseAsync(connection, "a17").ConfigureAwait(false);
        Assert.AreEqual(1, listed.Count);
        Assert.IsTrue(listed[0].StartsWith("a17 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapRoundTripsInternationalMailboxNamesForThunderbird scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapRoundTripsInternationalMailboxNamesForThunderbird()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 CREATE Projects").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 CREATE \"Projects/&ZeVnLIqe-\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5 CREATE \"Projects/&U,BTFw-\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 LIST \"\" \"Projects/*\"").ConfigureAwait(false);
        var listed = await ReadUntilTaggedResponseAsync(connection, "a6").ConfigureAwait(false);
        Assert.IsTrue(listed.Any(line =>
            line.EndsWith("\"Projects/&ZeVnLIqe-\"", StringComparison.Ordinal)));
        Assert.IsTrue(listed.Any(line =>
            line.EndsWith("\"Projects/&U,BTFw-\"", StringComparison.Ordinal)));

        const string message =
            "From: user@tenant.example.test\r\n" +
            "To: user@tenant.example.test\r\n" +
            "Subject: international mailbox\r\n" +
            "\r\n" +
            "body\r\n";
        await connection.WriteLineAsync($"a7 APPEND \"Projects/&ZeVnLIqe-\" {{{message.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK [APPENDUID", StringComparison.Ordinal));

        await connection.WriteLineAsync("a8 SELECT \"Projects/&ZeVnLIqe-\"").ConfigureAwait(false);
        var selected = await ReadUntilTaggedResponseAsync(connection, "a8").ConfigureAwait(false);
        Assert.IsTrue(selected[^1].StartsWith("a8 OK [READ-WRITE]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a9 COPY 1 \"Projects/&U,BTFw-\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a9 OK [COPYUID", StringComparison.Ordinal));

        await connection.WriteLineAsync("a10 STATUS \"Projects/&U,BTFw-\" (MESSAGES)").ConfigureAwait(false);
        Assert.AreEqual(
            "* STATUS \"Projects/&U,BTFw-\" (MESSAGES 1)",
            await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a10 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 GETQUOTAROOT \"Projects/&ZeVnLIqe-\"").ConfigureAwait(false);
        var quota = await ReadUntilTaggedResponseAsync(connection, "a11").ConfigureAwait(false);
        Assert.AreEqual("* QUOTAROOT \"Projects/&ZeVnLIqe-\" \"\"", quota[0], StringComparer.Ordinal);

        await connection.WriteLineAsync("a12 RENAME \"Projects/&U,BTFw-\" \"Projects/A&-B\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a12 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a13 SELECT \"Projects/A&-B\"").ConfigureAwait(false);
        selected = await ReadUntilTaggedResponseAsync(connection, "a13").ConfigureAwait(false);
        Assert.IsTrue(selected[^1].StartsWith("a13 OK [READ-WRITE]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a14 MOVE 1 \"Projects/&ZeVnLIqe-\"").ConfigureAwait(false);
        var moved = await ReadUntilTaggedResponseAsync(connection, "a14").ConfigureAwait(false);
        Assert.IsTrue(moved[^1].StartsWith("a14 OK [COPYUID", StringComparison.Ordinal));

        await connection.WriteLineAsync("a15 STATUS \"Projects/&ZeVnLIqe-\" (MESSAGES)").ConfigureAwait(false);
        Assert.AreEqual(
            "* STATUS \"Projects/&ZeVnLIqe-\" (MESSAGES 2)",
            await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a15 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a16 DELETE \"Projects/A&-B\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a16 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a17 CREATE \"&AEE-\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a17 BAD", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapUtf8AcceptUsesDirectMailboxNamesSearchAndInternationalizedHeaders scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapUtf8AcceptUsesDirectMailboxNamesSearchAndInternationalizedHeaders()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteUtf8LineAsync("a3 CREATE \"Projects/Žuta pošta\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 BAD", StringComparison.Ordinal));

        const string internationalMessage =
            "From: user@tenant.example.test\r\n" +
            "To: user@tenant.example.test\r\n" +
            "Subject: Žuta pošta\r\n" +
            "\r\n" +
            "Pozdrav\r\n";
        var internationalMessageSize = Encoding.UTF8.GetByteCount(internationalMessage);
        await connection.WriteLineAsync($"a4 APPEND INBOX {{{internationalMessageSize}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteUtf8RawAsync(internationalMessage).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 NO [CANNOT]", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 ENABLE UTF8=ACCEPT").ConfigureAwait(false);
        Assert.AreEqual("* ENABLED UTF8=ACCEPT", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 CREATE Projects").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("a7 CREATE \"Projects/Cafe\u0301\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("a8 CREATE \"Projects/Žuta pošta\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a8 OK", StringComparison.Ordinal));

        var invalidUtf8Command = Encoding.ASCII.GetBytes("a9 CREATE \"Projects/")
            .Concat(new byte[] { 0xc3, 0x28 })
            .Concat(Encoding.ASCII.GetBytes("\"\r\n"))
            .ToArray();
        await connection.WriteBytesAsync(invalidUtf8Command).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a9 BAD", StringComparison.Ordinal));

        await connection.WriteUtf8LineAsync("a10 CREATE \"Projects/bad\u2028name\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a10 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 LIST \"\" \"Projects/*\"").ConfigureAwait(false);
        var listed = new List<string>();
        string line;
        do
        {
            line = await connection.ReadUtf8LineAsync().ConfigureAwait(false);
            listed.Add(line);
        }
        while (!line.StartsWith("a11 ", StringComparison.Ordinal));
        Assert.IsTrue(listed.Any(value => value.EndsWith("\"Projects/Café\"", StringComparison.Ordinal)));
        Assert.IsTrue(listed.Any(value => value.EndsWith("\"Projects/Žuta pošta\"", StringComparison.Ordinal)));

        await connection.WriteUtf8LineAsync("a12 STATUS \"Projects/Žuta pošta\" (MESSAGES)").ConfigureAwait(false);
        Assert.AreEqual(
            "* STATUS \"Projects/Žuta pošta\" (MESSAGES 0)",
            await connection.ReadUtf8LineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a12 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync($"a13 APPEND INBOX {{{internationalMessageSize}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteUtf8RawAsync(internationalMessage).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a13 OK [APPENDUID", StringComparison.Ordinal));
        Assert.AreEqual("Žuta pošta", (await server.GetStoredEmailAsync(DefaultFolders.Inbox).ConfigureAwait(false)).Subject, StringComparer.Ordinal);

        var invalidHeaderPrefix = Encoding.ASCII.GetBytes(
            "From: user@tenant.example.test\r\nTo: user@tenant.example.test\r\nSubject: ");
        var invalidHeaderMessage = invalidHeaderPrefix
            .Concat(new byte[] { 0xc3, 0x28 })
            .Concat(Encoding.ASCII.GetBytes("\r\n\r\nbody\r\n"))
            .ToArray();
        await connection.WriteLineAsync($"a14 APPEND INBOX {{{invalidHeaderMessage.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteBytesAsync(invalidHeaderMessage).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a14 NO [CANNOT]", StringComparison.Ordinal));

        await connection.WriteLineAsync("a15 SELECT INBOX").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a15").ConfigureAwait(false);
        await connection.WriteLineAsync("a16 UID SEARCH CHARSET UTF-8 ALL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a16 BAD", StringComparison.Ordinal));
        await connection.WriteUtf8LineAsync("a17 UID SEARCH SUBJECT \"Žuta\"").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a17 OK", StringComparison.Ordinal));

        await connection.WriteUtf8LineAsync("a18 SELECT \"Projects/Žuta pošta\"").ConfigureAwait(false);
        var selected = await ReadUntilTaggedResponseAsync(connection, "a18").ConfigureAwait(false);
        Assert.IsTrue(selected[^1].StartsWith("a18 OK [READ-WRITE]", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapListExtendedCombinesSubscriptionsSpecialUseAndStatus scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapListExtendedCombinesSubscriptionsSpecialUseAndStatus()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 CREATE Projects").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 CREATE Projects/2026").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5 CREATE Projects/2027").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5d CREATE Drafts").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5d OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a5s CREATE Spam").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5s OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a6 UNSUBSCRIBE Projects").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a7 UNSUBSCRIBE Projects/2026").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a8 LIST (SUBSCRIBED RECURSIVEMATCH) \"\" \"%\" " +
            "RETURN (SUBSCRIBED CHILDREN STATUS (MESSAGES UIDNEXT SIZE))").ConfigureAwait(false);
        var recursive = await ReadUntilTaggedResponseAsync(connection, "a8").ConfigureAwait(false);
        var projects = recursive.Single(line =>
            line.StartsWith("* LIST (", StringComparison.Ordinal)
            && line.Contains("\"Projects\" (CHILDINFO (\"SUBSCRIBED\"))", StringComparison.Ordinal));
        Assert.IsFalse(projects.Contains("\\Subscribed", StringComparison.Ordinal));
        Assert.IsFalse(recursive.Any(line =>
            line.StartsWith("* STATUS \"Projects\"", StringComparison.Ordinal)));

        await connection.WriteLineAsync(
            "a9 LIST (SUBSCRIBED) \"\" (\"Projects/*\" \"Sent\") " +
            "RETURN (SUBSCRIBED STATUS (MESSAGES SIZE))").ConfigureAwait(false);
        var subscribed = await ReadUntilTaggedResponseAsync(connection, "a9").ConfigureAwait(false);
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

        await connection.WriteLineAsync("a10 LIST (SPECIAL-USE) \"\" \"*\"").ConfigureAwait(false);
        var specialUse = await ReadUntilTaggedResponseAsync(connection, "a10").ConfigureAwait(false);
        Assert.AreEqual(4, specialUse.Count(line => line.StartsWith("* LIST", StringComparison.Ordinal)));
        Assert.IsTrue(specialUse.Any(line => line.Contains("\\Drafts", StringComparison.Ordinal)));
        Assert.IsTrue(specialUse.Any(line => line.Contains("\\Junk", StringComparison.Ordinal)));
        Assert.IsTrue(specialUse.Any(line => line.Contains("\\Sent", StringComparison.Ordinal)));
        Assert.IsTrue(specialUse.Any(line => line.Contains("\\Trash", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a11 LIST (UNKNOWN) \"\" \"*\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a11 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a12 LIST (RECURSIVEMATCH) \"\" \"*\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a12 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a13 LIST \"\" \"\"").ConfigureAwait(false);
        Assert.AreEqual("* LIST (\\Noselect) \"/\" \"\"", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a13 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapSearchMatchesOnlyTheRequestedHeaderValue()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedInboxMessagesForSearchAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);

        string response;
        do
        {
            response = await connection.ReadLineAsync().ConfigureAwait(false);
        }
        while (!response.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 UID SEARCH HEADER X-Mk8-Test mixedmarker42").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 UID SEARCH SUBJECT mixedcasesubject").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 UID SEARCH HEADER X-Mk8-Test absent-marker").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH", (await connection.ReadLineAsync().ConfigureAwait(false)).TrimEnd(), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapSearchEvaluatesBooleanGroupsAndBoundedMessageSets scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapSearchEvaluatesBooleanGroupsAndBoundedMessageSets()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedInboxMessagesForSearchAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);

        string response;
        do
        {
            response = await connection.ReadLineAsync().ConfigureAwait(false);
        }
        while (!response.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a4 UID SEARCH OR SUBJECT MixedCaseSubject SUBJECT \"Other subject\"").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1 2", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a5 UID SEARCH NOT (OR SUBJECT MixedCaseSubject SUBJECT \"Other subject\")").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 3", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a6 UID SEARCH SUBJECT \"Third \\\"quoted\\\" subject\"").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 3", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a7 UID SEARCH SUBJECT \"Other subject\" NOT FROM third-header@example.net").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 2", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a8 SEARCH 2:*").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 2 3", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a8 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a9 UID SEARCH UID 1:2147483647").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1 2 3", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a9 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a10 UID SEARCH RETURN (MIN MAX COUNT ALL) SUBJECT absent-marker").ConfigureAwait(false);
        Assert.AreEqual("* ESEARCH (TAG \"a10\") UID COUNT 0", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a10 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 SEARCH RETURN (MIN MAX COUNT ALL) ALL").ConfigureAwait(false);
        Assert.AreEqual(
            "* ESEARCH (TAG \"a11\") MIN 1 MAX 3 COUNT 3 ALL 1:3",
            await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a11 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapSearchResSavesUidStableResultsAcrossCommandsAndExpunges scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapSearchResSavesUidStableResultsAcrossCommandsAndExpunges()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedInboxMessagesForSearchAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);

        await connection.WriteLineAsync(
            "a4 UID SEARCH RETURN (SAVE) OR SUBJECT MixedCaseSubject SUBJECT \"Other subject\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 SEARCH $").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1 2", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a6 UID SEARCH UID $").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1 2", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 UID SEARCH RETURN (SAVE MIN MAX) ALL").ConfigureAwait(false);
        Assert.AreEqual(
            "* ESEARCH (TAG \"a7\") UID MIN 1 MAX 3",
            await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a8 FETCH $ (UID)").ConfigureAwait(false);
        var fetched = await ReadUntilTaggedResponseAsync(connection, "a8").ConfigureAwait(false);
        Assert.IsTrue(fetched.Any(line => line.StartsWith("* 1 FETCH (UID 1", StringComparison.Ordinal)));
        Assert.IsTrue(fetched.Any(line => line.StartsWith("* 3 FETCH (UID 3", StringComparison.Ordinal)));
        Assert.AreEqual(3, fetched.Count);

        await connection.WriteLineAsync("a9 STORE $ +FLAGS.SILENT (\\Flagged)").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a9 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a10 UID SEARCH FLAGGED").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1 3", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a10 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 UID STORE 1 +FLAGS.SILENT (\\Deleted)").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a11 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a12 UID EXPUNGE $").ConfigureAwait(false);
        Assert.AreEqual("* 1 EXPUNGE", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a12 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a13 UID FETCH $ (UID)").ConfigureAwait(false);
        fetched = await ReadUntilTaggedResponseAsync(connection, "a13").ConfigureAwait(false);
        Assert.AreEqual(2, fetched.Count);
        Assert.IsTrue(fetched[0].StartsWith("* 2 FETCH (UID 3", StringComparison.Ordinal));

        await connection.WriteLineAsync("a14 UID SEARCH RETURN (SAVE) SUBJECT absent-marker").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a14 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a15 COPY $ Trash").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a15 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a16 UID FETCH $ (UID)").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a16 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a17 UID SEARCH RETURN (SAVE) UID 2").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a17 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a18 UID SEARCH RETURN (SAVE UNKNOWN) ALL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a18 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a19 UID FETCH $ (UID)").ConfigureAwait(false);
        fetched = await ReadUntilTaggedResponseAsync(connection, "a19").ConfigureAwait(false);
        Assert.AreEqual(2, fetched.Count);
        Assert.IsTrue(fetched[0].StartsWith("* 1 FETCH (UID 2", StringComparison.Ordinal));

        await connection.WriteLineAsync("a20 UID SEARCH RETURN (SAVE) CHARSET KOI8-R ALL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
            "a20 NO [BADCHARSET",
            StringComparison.Ordinal));
        await connection.WriteLineAsync("a21 UID FETCH $ (UID)").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a21 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a22 UID SEARCH RETURN (SAVE) UID 2").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a22 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a23 SELECT INBOX").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a23").ConfigureAwait(false);
        await connection.WriteLineAsync("a24 UID FETCH $ (UID)").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a24 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapSearchUsesHeaderBodyInternalAndSentDateSemantics scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapSearchUsesHeaderBodyInternalAndSentDateSemantics()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedInboxMessagesForSearchAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);

        string response;
        do
        {
            response = await connection.ReadLineAsync().ConfigureAwait(false);
        }
        while (!response.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 UID SEARCH HEADER X-Unrelated mixedmarker42").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 2", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 UID SEARCH TEXT \"prefix mixedmarker42\"").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 UID SEARCH BODY body-only-needle").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 3", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 UID SEARCH FROM third-header@example.net").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 3", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a8 UID SEARCH BCC hidden@example.net").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 3", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a8 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a9 UID SEARCH SENTON 5-Feb-2026").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a9 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a10 UID SEARCH ON 2-Jan-2026").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 2", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a10 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a11 UID SEARCH NEW").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH", (await connection.ReadLineAsync().ConfigureAwait(false)).TrimEnd(), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a11 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a12 UID SEARCH RECENT").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH", (await connection.ReadLineAsync().ConfigureAwait(false)).TrimEnd(), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a12 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a13 UID SEARCH OLD").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1 2 3", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a13 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ImapSearchRejectsInvalidCriteriaAndKeepsTheConnectionUsable()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedInboxMessagesForSearchAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);

        string response;
        do
        {
            response = await connection.ReadLineAsync().ConfigureAwait(false);
        }
        while (!response.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 UID SEARCH CHARSET UTF-8 ALL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
            "a4 NO [BADCHARSET (US-ASCII)]",
            StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 UID SEARCH UNKNOWN-CRITERION").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 UID SEARCH OR SUBJECT one").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 UID SEARCH NOT (ALL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a8 UID SEARCH SINCE invalid-date").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a8 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync("a9 NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a9 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapSortImplementsEveryRfc5256KeyCharsetAndTieBreak scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapSortImplementsEveryRfc5256KeyCharsetAndTieBreak()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedInboxMessagesForSortAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);

        await AssertSortAsync("a4 SORT (ARRIVAL) US-ASCII ALL", "* SORT 2 4 3 1 5").ConfigureAwait(false);
        await AssertSortAsync("a5 UID SORT (DATE) US-ASCII ALL", "* SORT 30 40 10 50 20").ConfigureAwait(false);
        await AssertSortAsync("a6 UID SORT (FROM) \"US-ASCII\" ALL", "* SORT 20 40 30 10 50").ConfigureAwait(false);
        await AssertSortAsync("a7 UID SORT (TO) US-ASCII ALL", "* SORT 30 10 50 40 20").ConfigureAwait(false);
        await AssertSortAsync("a8 UID SORT (CC) US-ASCII ALL", "* SORT 30 40 20 10 50").ConfigureAwait(false);
        await AssertSortAsync("a9 UID SORT (SUBJECT) UTF-8 ALL", "* SORT 40 30 10 20 50").ConfigureAwait(false);
        await AssertSortAsync("a10 UID SORT (SIZE) US-ASCII ALL", "* SORT 20 40 30 10 50").ConfigureAwait(false);
        await AssertSortAsync("a11 UID SORT (REVERSE SIZE) US-ASCII ALL", "* SORT 10 50 30 40 20").ConfigureAwait(false);
        await AssertSortAsync(
            "a12 UID SORT (SUBJECT REVERSE DATE) UTF-8 SUBJECT topic",
            "* SORT 20 10 50").ConfigureAwait(false);
        await AssertSortAsync(
            "a12e UID SORT (DATE) US-ASCII SUBJECT absent-marker",
            "* SORT").ConfigureAwait(false);

        await connection.WriteUtf8LineAsync(
            "a13 UID SORT (ARRIVAL) UTF-8 SUBJECT \"Äpfel\"").ConfigureAwait(false);
        Assert.AreEqual("* SORT 30", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a13 OK", StringComparison.Ordinal));

        await AssertSortAsync(
            "a13m UID SORT (ARRIVAL) US-ASCII MODSEQ 3",
            "* SORT 40 30 50 (MODSEQ 5)").ConfigureAwait(false);
        await connection.WriteLineAsync("a13s UID SEARCH MODSEQ 4").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 40 50 (MODSEQ 5)", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a13s OK", StringComparison.Ordinal));
        await connection.WriteLineAsync(
            "a13x UID SEARCH RETURN (ALL) MODSEQ \"/flags/\\\\Seen\" all 4").ConfigureAwait(false);
        Assert.AreEqual(
            "* ESEARCH (TAG \"a13x\") UID ALL 40,50 MODSEQ 5",
            await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a13x OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a13z UID SEARCH MODSEQ 0").ConfigureAwait(false);
        Assert.AreEqual(
            "* SEARCH 10 20 30 40 50 (MODSEQ 5)",
            await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a13z OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a14 UID SORT () US-ASCII ALL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a14 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a15 UID SORT (REVERSE) US-ASCII ALL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a15 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a16 UID SORT (UNKNOWN) US-ASCII ALL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a16 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a17 UID SORT (DATE) KOI8-R ALL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
            "a17 NO [BADCHARSET (US-ASCII UTF-8)]",
            StringComparison.Ordinal));
        await connection.WriteLineAsync("a18 NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a18 OK", StringComparison.Ordinal));

        async Task AssertSortAsync(string command, string expected)
        {
            await connection.WriteLineAsync(command).ConfigureAwait(false);
            Assert.AreEqual(expected, await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
            var tag = command[..command.IndexOf(' ', StringComparison.Ordinal)];
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
                $"{tag} OK",
                StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapOrderedSubjectThreadingUsesBaseSubjectSentDateAndSiblingBranches scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapOrderedSubjectThreadingUsesBaseSubjectSentDateAndSiblingBranches()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedInboxMessagesForSortAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);

        await AssertThreadAsync(
            "a4 THREAD ORDEREDSUBJECT US-ASCII ALL",
            "* THREAD (3)(4)(1 (5)(2))").ConfigureAwait(false);
        await AssertThreadAsync(
            "a5 UID THREAD ORDEREDSUBJECT \"US-ASCII\" ALL",
            "* THREAD (30)(40)(10 (50)(20))").ConfigureAwait(false);
        await AssertThreadAsync(
            "a6 UID THREAD ORDEREDSUBJECT UTF-8 SUBJECT topic",
            "* THREAD (10 (50)(20))").ConfigureAwait(false);
        await connection.WriteUtf8LineAsync(
            "a7 UID THREAD ORDEREDSUBJECT UTF-8 SUBJECT \"Äpfel\"").ConfigureAwait(false);
        Assert.AreEqual("* THREAD (30)", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK", StringComparison.Ordinal));
        await AssertThreadAsync(
            "a8 UID THREAD ORDEREDSUBJECT US-ASCII MODSEQ 4",
            "* THREAD (40)(50)").ConfigureAwait(false);
        await AssertThreadAsync(
            "a8b UID THREAD ORDEREDSUBJECT US-ASCII UID 10,20",
            "* THREAD (10 20)").ConfigureAwait(false);
        await AssertThreadAsync(
            "a9 UID THREAD ORDEREDSUBJECT US-ASCII SUBJECT absent-marker",
            "* THREAD").ConfigureAwait(false);

        await connection.WriteLineAsync("a10 UID THREAD ORDEREDSUBJECT KOI8-R ALL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
            "a10 NO [BADCHARSET (US-ASCII UTF-8)]",
            StringComparison.Ordinal));
        await connection.WriteLineAsync("a11 UID THREAD UNKNOWN US-ASCII ALL").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a11 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a12 UID THREAD ORDEREDSUBJECT US-ASCII").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a12 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a13 NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a13 OK", StringComparison.Ordinal));

        async Task AssertThreadAsync(string command, string expected)
        {
            await connection.WriteLineAsync(command).ConfigureAwait(false);
            Assert.AreEqual(expected, await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
            var tag = command[..command.IndexOf(' ', StringComparison.Ordinal)];
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
                $"{tag} OK",
                StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapReferencesThreadingImplementsTheCompleteContainerAlgorithm scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapReferencesThreadingImplementsTheCompleteContainerAlgorithm()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedInboxMessagesForReferencesAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);

        await AssertThreadAsync(
            "a4 UID THREAD REFERENCES US-ASCII UID 101:119",
            "* THREAD (101 (103)(102))((104)(105))(106 107)(108)" +
            "(110 109)(111 113)(112)(114)(115)(117 116)(118 119)").ConfigureAwait(false);
        await AssertThreadAsync(
            "a5 THREAD REFERENCES US-ASCII UID 101:119",
            "* THREAD (1 (3)(2))((4)(5))(6 7)(8)(10 9)" +
            "(11 13)(12)(14)(15)(17 16)(18 19)").ConfigureAwait(false);
        await AssertThreadAsync(
            "a6 UID THREAD REFERENCES US-ASCII UID 102:103",
            "* THREAD ((103)(102))").ConfigureAwait(false);
        await AssertThreadAsync(
            "a7 UID THREAD REFERENCES US-ASCII UID 102",
            "* THREAD (102)").ConfigureAwait(false);
        await AssertThreadAsync(
            "a8 UID THREAD REFERENCES US-ASCII UID 201:203",
            "* THREAD ((201 202)(203))").ConfigureAwait(false);
        await AssertThreadAsync(
            "a9 UID THREAD REFERENCES US-ASCII UID 204:205",
            "* THREAD (205 204)").ConfigureAwait(false);
        await AssertThreadAsync(
            "a10 UID THREAD REFERENCES US-ASCII UID 206:207",
            "* THREAD (206)(207)").ConfigureAwait(false);
        await AssertThreadAsync(
            "a11 UID THREAD REFERENCES US-ASCII UID 208:212",
            "* THREAD ((208)(209)(210)(211)(212))").ConfigureAwait(false);
        await connection.WriteUtf8LineAsync(
            "a12 UID THREAD REFERENCES UTF-8 SUBJECT \"Äpfel\"").ConfigureAwait(false);
        Assert.AreEqual("* THREAD (118 119)", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a12 OK", StringComparison.Ordinal));
        await AssertThreadAsync(
            "a13 UID THREAD REFERENCES US-ASCII UID 118:119 MODSEQ 18",
            "* THREAD (118 119)").ConfigureAwait(false);
        await AssertThreadAsync(
            "a14 UID THREAD REFERENCES US-ASCII SUBJECT absent-marker",
            "* THREAD").ConfigureAwait(false);
        await connection.WriteLineAsync("a15 NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a15 OK", StringComparison.Ordinal));

        async Task AssertThreadAsync(string command, string expected)
        {
            await connection.WriteLineAsync(command).ConfigureAwait(false);
            Assert.AreEqual(expected, await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
            var tag = command[..command.IndexOf(' ', StringComparison.Ordinal)];
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
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
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        const string message =
            "From: user@tenant.example.test\r\n" +
            "To: user@tenant.example.test\r\n" +
            "Subject: café\r\n" +
            "\r\n" +
            "\r\nbody é\r\n";
        await connection.WriteLineAsync("a3 ENABLE UTF8=ACCEPT").ConfigureAwait(false);
        Assert.AreEqual("* ENABLED UTF8=ACCEPT", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK", StringComparison.Ordinal));

        var messageSize = Encoding.UTF8.GetByteCount(message);
        await connection.WriteLineAsync($"a4 APPEND \"Sent\" {{{messageSize}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteUtf8RawAsync(message).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK [APPENDUID", StringComparison.Ordinal));

        var stored = await server.GetStoredEmailAsync(DefaultFolders.Sent).ConfigureAwait(false);
        Assert.AreEqual("café", stored.Subject, StringComparer.Ordinal);
        Assert.AreEqual("\r\nbody é\r\n", stored.Body, StringComparer.Ordinal);
        Assert.AreEqual(messageSize, stored.SizeBytes);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(message), stored.RawMessage!);
        var persisted = await server.GetPersistedEmailAsync(DefaultFolders.Sent).ConfigureAwait(false);
        Assert.IsNull(persisted.RawMessage);
        Assert.AreEqual(LargeObjectProviders.AzureBlob, persisted.RawMessageObjectProvider, StringComparer.Ordinal);
        Assert.IsNotNull(persisted.RawMessageObjectName);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapMultiAppendReportsAppendLimitAndMailboxSize()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        const string firstMessage =
            "From: user@tenant.example.test\r\n" +
            "To: user@tenant.example.test\r\n" +
            "Subject: first\r\n" +
            "\r\n" +
            "first body\r\n";
        const string secondMessage =
            "From: user@tenant.example.test\r\n" +
            "To: user@tenant.example.test\r\n" +
            "Subject: second\r\n" +
            "\r\n" +
            "second body\r\n";

        await connection.WriteLineAsync($"a3 APPEND \"Sent\" {{{firstMessage.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(firstMessage).ConfigureAwait(false);
        await connection.WriteLineAsync($" (\\Seen) {{{secondMessage.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(secondMessage).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        var appendResponse = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.IsTrue(appendResponse.StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));
        StringAssert.Contains(appendResponse, " 1:2]", StringComparison.Ordinal);

        await connection.WriteLineAsync("a4 STATUS \"Sent\" (MESSAGES UIDNEXT SIZE)").ConfigureAwait(false);
        Assert.AreEqual(
            $"* STATUS \"Sent\" (MESSAGES 2 UIDNEXT 3 SIZE {firstMessage.Length + secondMessage.Length})",
            await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));

        var stored = await server.GetStoredEmailsAsync(DefaultFolders.Sent).ConfigureAwait(false);
        Assert.AreEqual(2, stored.Count);
        Assert.IsFalse(stored[0].IsRead);
        Assert.IsTrue(stored[1].IsRead);

        await connection.WriteLineAsync($"a5 APPEND \"Sent\" {{{firstMessage.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(firstMessage).ConfigureAwait(false);
        await connection.WriteLineAsync(" invalid continuation").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 BAD", StringComparison.Ordinal));
        Assert.AreEqual(2, await server.CountStoredEmailsAsync().ConfigureAwait(false));

        await connection.WriteLineAsync("a6 APPEND \"Sent\" {0}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 NO", StringComparison.Ordinal));
        Assert.AreEqual(2, await server.CountStoredEmailsAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [Timeout(15_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapPersistsCustomKeywordsAcrossAppendStoreSearchAndCopy scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapPersistsCustomKeywordsAcrossAppendStoreSearchAndCopy()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        const string message =
            "From: user@tenant.example.test\r\n" +
            "To: user@tenant.example.test\r\n" +
            "Subject: keyword persistence\r\n" +
            "\r\n" +
            "tagged message\r\n";
        await connection.WriteLineAsync(
            $"a3 APPEND \"Sent\" (\\Seen $label1 Custom) {{{message.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 SELECT \"Sent\"").ConfigureAwait(false);
        var responses = await ReadUntilTaggedResponseAsync(connection, "a4").ConfigureAwait(false);
        var definedFlags = responses.Single(line => line.StartsWith("* FLAGS (", StringComparison.Ordinal));
        StringAssert.Contains(definedFlags, "$label1", StringComparison.Ordinal);
        StringAssert.Contains(definedFlags, "Custom", StringComparison.Ordinal);
        Assert.IsTrue(responses.Any(line => line.Contains("PERMANENTFLAGS", StringComparison.Ordinal)
            && line.Contains("\\*", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a5 FETCH 1 (UID FLAGS)").ConfigureAwait(false);
        responses = await ReadUntilTaggedResponseAsync(connection, "a5").ConfigureAwait(false);
        Assert.IsTrue(responses.Any(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal)
            && line.Contains("\\Seen", StringComparison.Ordinal)
            && line.Contains("$label1", StringComparison.Ordinal)
            && line.Contains("Custom", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a6 STORE 1 +FLAGS ($label2)").ConfigureAwait(false);
        responses = await ReadUntilTaggedResponseAsync(connection, "a6").ConfigureAwait(false);
        Assert.IsTrue(responses.Any(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal)
            && line.Contains("$label1", StringComparison.Ordinal)
            && line.Contains("$label2", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a7 UID STORE 1 -FLAGS (CUSTOM)").ConfigureAwait(false);
        responses = await ReadUntilTaggedResponseAsync(connection, "a7").ConfigureAwait(false);
        Assert.IsTrue(responses.Any(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal)
            && line.Contains("$label1", StringComparison.Ordinal)
            && line.Contains("$label2", StringComparison.Ordinal)
            && !line.Contains("Custom", StringComparison.OrdinalIgnoreCase)));

        await connection.WriteLineAsync("a8 UID SEARCH KEYWORD $label2").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a8 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a9 COPY 1 Trash").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a9 OK [COPYUID", StringComparison.Ordinal));
        await connection.WriteLineAsync("a10 SELECT Trash").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a10").ConfigureAwait(false);
        await connection.WriteLineAsync("a11 FETCH 1 FLAGS").ConfigureAwait(false);
        responses = await ReadUntilTaggedResponseAsync(connection, "a11").ConfigureAwait(false);
        Assert.IsTrue(responses.Any(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal)
            && line.Contains("$label1", StringComparison.Ordinal)
            && line.Contains("$label2", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a12 STORE 1 FLAGS (\\Seen Final)").ConfigureAwait(false);
        responses = await ReadUntilTaggedResponseAsync(connection, "a12").ConfigureAwait(false);
        var replacement = responses.Single(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal));
        StringAssert.Contains(replacement, "\\Seen", StringComparison.Ordinal);
        StringAssert.Contains(replacement, "Final", StringComparison.Ordinal);
        Assert.IsFalse(replacement.Contains("$label", StringComparison.Ordinal));

        await connection.WriteLineAsync("a13 UID SEARCH UNKEYWORD Final").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a13 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a14 STORE 1 +FLAGS (bad])").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a14 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync($"a15 APPEND \"Sent\" (\\Recent) {{{message.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a15 NO [CANNOT]", StringComparison.Ordinal));

        var excessiveKeywords = string.Join(' ', Enumerable.Range(1, 129).Select(index => $"k{index}"));
        await connection.WriteLineAsync($"a16 STORE 1 +FLAGS ({excessiveKeywords})").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a16 NO [LIMIT]", StringComparison.Ordinal));

        await connection.WriteLineAsync("a17 FETCH 1 FLAGS").ConfigureAwait(false);
        responses = await ReadUntilTaggedResponseAsync(connection, "a17").ConfigureAwait(false);
        replacement = responses.Single(line => line.StartsWith("* 1 FETCH", StringComparison.Ordinal));
        StringAssert.Contains(replacement, "Final", StringComparison.Ordinal);
        Assert.IsFalse(replacement.Contains("bad]", StringComparison.Ordinal));
        Assert.IsFalse(replacement.Contains("k1", StringComparison.Ordinal));

        var sent = await server.GetStoredEmailAsync(DefaultFolders.Sent).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(ExpectedVector1, sent.Keywords);
        var copied = await server.GetStoredEmailAsync(DefaultFolders.Trash).ConfigureAwait(false);
        CollectionAssert.AreEqual(ExpectedVector2, copied.Keywords);
        Assert.IsTrue(copied.IsRead);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(25_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapIdleReportsCrossConnectionMailboxChanges scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapIdleReportsCrossConnectionMailboxChanges(bool enableQresync)
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var writerConnection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var writerConnectionLifetime = writerConnection.ConfigureAwait(false);
        var idleConnection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var idleConnectionLifetime = idleConnection.ConfigureAwait(false);

        await writerConnection.ReadLineAsync().ConfigureAwait(false);
        await writerConnection.WriteLineAsync("w1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await writerConnection.ReadLineAsync().ConfigureAwait(false)).StartsWith("w1 OK", StringComparison.Ordinal));
        await writerConnection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await writerConnection.WriteLineAsync($"w2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await writerConnection.ReadLineAsync().ConfigureAwait(false)).StartsWith("w2 OK", StringComparison.Ordinal));

        await idleConnection.ReadLineAsync().ConfigureAwait(false);
        await idleConnection.WriteLineAsync("r1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await idleConnection.ReadLineAsync().ConfigureAwait(false)).StartsWith("r1 OK", StringComparison.Ordinal));
        await idleConnection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await idleConnection.WriteLineAsync($"r2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await idleConnection.ReadLineAsync().ConfigureAwait(false)).StartsWith("r2 OK", StringComparison.Ordinal));

        const string firstMessage =
            "From: user@tenant.example.test\r\nTo: user@tenant.example.test\r\n" +
            "Subject: first idle message\r\n\r\nfirst\r\n";
        const string secondMessage =
            "From: user@tenant.example.test\r\nTo: user@tenant.example.test\r\n" +
            "Subject: second idle message\r\n\r\nsecond\r\n";
        const string thirdMessage =
            "From: user@tenant.example.test\r\nTo: user@tenant.example.test\r\n" +
            "Subject: third idle message\r\n\r\nthird\r\n";

        await writerConnection.WriteLineAsync($"w3 APPEND INBOX {{{firstMessage.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await writerConnection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await writerConnection.WriteRawAsync(firstMessage).ConfigureAwait(false);
        await writerConnection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await writerConnection.ReadLineAsync().ConfigureAwait(false)).StartsWith("w3 OK", StringComparison.Ordinal));
        await writerConnection.WriteLineAsync($"w4 APPEND INBOX {{{secondMessage.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await writerConnection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await writerConnection.WriteRawAsync(secondMessage).ConfigureAwait(false);
        await writerConnection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await writerConnection.ReadLineAsync().ConfigureAwait(false)).StartsWith("w4 OK", StringComparison.Ordinal));

        var selectTag = "r3";
        var idleTag = "r4";
        if (enableQresync)
        {
            await idleConnection.WriteLineAsync("r3 ENABLE QRESYNC").ConfigureAwait(false);
            Assert.AreEqual("* ENABLED QRESYNC", await idleConnection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
            Assert.IsTrue((await idleConnection.ReadLineAsync().ConfigureAwait(false)).StartsWith("r3 OK", StringComparison.Ordinal));
            selectTag = "r4";
            idleTag = "r5";
        }

        await idleConnection.WriteLineAsync($"{selectTag} SELECT INBOX (CONDSTORE)").ConfigureAwait(false);
        var selected = await ReadUntilTaggedResponseAsync(idleConnection, selectTag).ConfigureAwait(false);
        Assert.IsTrue(selected[^1].StartsWith($"{selectTag} OK", StringComparison.Ordinal));
        await idleConnection.WriteLineAsync($"{idleTag} IDLE").ConfigureAwait(false);
        Assert.AreEqual("+ idling", await idleConnection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);

        await writerConnection.WriteLineAsync("w5 SELECT INBOX").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(writerConnection, "w5").ConfigureAwait(false);
        await writerConnection.WriteLineAsync("w6 STORE 1 +FLAGS (\\Seen $label1)").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(writerConnection, "w6").ConfigureAwait(false);
        await writerConnection.WriteLineAsync("w7 MOVE 2 Trash").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(writerConnection, "w7").ConfigureAwait(false);
        await writerConnection.WriteLineAsync($"w8 APPEND INBOX {{{thirdMessage.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await writerConnection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await writerConnection.WriteRawAsync(thirdMessage).ConfigureAwait(false);
        await writerConnection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await writerConnection.ReadLineAsync().ConfigureAwait(false)).StartsWith("w8 OK", StringComparison.Ordinal));

        var expectedRemoval = enableQresync ? "* VANISHED 2" : "* 2 EXPUNGE";
        var updates = new List<string>();
        for (var index = 0; index < 10; index++)
        {
            updates.Add(await idleConnection.ReadLineAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false));
            if (updates.Any(line => string.Equals(line, expectedRemoval, StringComparison.Ordinal))
                && updates.Any(line => string.Equals(line, "* 2 EXISTS", StringComparison.Ordinal))
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

        await idleConnection.WriteLineAsync("DONE").ConfigureAwait(false);
        var completed = await ReadUntilTaggedResponseAsync(idleConnection, idleTag).ConfigureAwait(false);
        Assert.IsTrue(completed[^1].StartsWith($"{idleTag} OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAppendChecksQuotaBeforeReadingTheLiteral()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        const string message =
            "From: user@tenant.example.test\r\n" +
            "To: user@tenant.example.test\r\n" +
            "Subject: quota\r\n" +
            "\r\n" +
            "body\r\n";
        await server.SetUserQuotaAsync(message.Length - 1).ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync($"a3 APPEND \"Sent\" {{{message.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 NO [OVERQUOTA]", StringComparison.Ordinal));
        Assert.AreEqual(0, await server.CountStoredEmailsAsync().ConfigureAwait(false));

        await server.SetUserQuotaAsync(message.Length).ConfigureAwait(false);
        await connection.WriteLineAsync($"a4 APPEND \"Sent\" {{{message.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK [APPENDUID", StringComparison.Ordinal));
        Assert.AreEqual(1, await server.CountStoredEmailsAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAppendRejectsLiteralWhenWorkerPreflightIsUnavailable()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(
            environment, port,
            applicationService: new UnavailableImapMailboxApplicationService(
                listingUnavailable: false)).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 APPEND INBOX {4}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
            "a3 NO [UNAVAILABLE]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 APPEND INBOX {4+}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
            "* BYE [UNAVAILABLE]", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAppendRejectsUnavailableOrMalformedWorkerCommitWithoutPartialSuccess()
    {
        const string message = "Subject: test\r\n\r\nbody";
        foreach (var malformed in new[] { false, true })
        {
            var port = ReservePort();
            var environment = CreateEnvironment(imapPort: port);

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var server = (await ServerFixture.StartImapAsync(
                environment, port,
                applicationService: new UnavailableImapMailboxApplicationService(
                    listingUnavailable: false,
                    appendCommitUnavailable: !malformed,
                    appendMalformed: malformed)).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var serverLifetime = server.ConfigureAwait(false);

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var connectionLifetime = connection.ConfigureAwait(false);

            await connection.ReadLineAsync().ConfigureAwait(false);
            await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
            await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync($"a3 APPEND INBOX {{{message.Length}}}").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
            await connection.WriteRawAsync(message).ConfigureAwait(false);
            await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
                "a3 NO [UNAVAILABLE]", StringComparison.Ordinal));
            await connection.WriteLineAsync("a4 NOOP").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapSearchFailsClosedWhenWorkerIsUnavailableOrMalformed()
    {
        foreach (var malformed in new[] { false, true })
        {
            var port = ReservePort();
            var environment = CreateEnvironment(imapPort: port);

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var server = (await ServerFixture.StartImapAsync(
                environment, port,
                applicationService: new UnavailableImapMailboxApplicationService(
                    listingUnavailable: false,
                    selectionAvailable: true,
                    searchMalformed: malformed)).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var serverLifetime = server.ConfigureAwait(false);

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var connectionLifetime = connection.ConfigureAwait(false);

            await connection.ReadLineAsync().ConfigureAwait(false);
            await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
            await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
            var selected = await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);
            Assert.IsTrue(selected[^1].StartsWith("a3 OK", StringComparison.Ordinal));

            await connection.WriteLineAsync("a4 SEARCH RETURN (SAVE ALL) ALL").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
                "a4 NO [UNAVAILABLE]", StringComparison.Ordinal));
            await connection.WriteLineAsync("a5 CAPABILITY").ConfigureAwait(false);
            var capability = await ReadUntilTaggedResponseAsync(connection, "a5").ConfigureAwait(false);
            Assert.IsTrue(capability[^1].StartsWith("a5 OK", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapSortFailsClosedWhenWorkerIsUnavailableOrMalformed()
    {
        foreach (var malformed in new[] { false, true })
        {
            var port = ReservePort();
            var environment = CreateEnvironment(imapPort: port);

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var server = (await ServerFixture.StartImapAsync(
                environment, port,
                applicationService: new UnavailableImapMailboxApplicationService(
                    listingUnavailable: false,
                    selectionAvailable: true,
                    sortMalformed: malformed)).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var serverLifetime = server.ConfigureAwait(false);

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var connectionLifetime = connection.ConfigureAwait(false);

            await connection.ReadLineAsync().ConfigureAwait(false);
            await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
            await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
            var selected = await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);
            Assert.IsTrue(selected[^1].StartsWith("a3 OK", StringComparison.Ordinal));

            await connection.WriteLineAsync("a4 SORT (SUBJECT) US-ASCII ALL").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
                "a4 NO [UNAVAILABLE]", StringComparison.Ordinal));
            await connection.WriteLineAsync("a5 CAPABILITY").ConfigureAwait(false);
            var capability = await ReadUntilTaggedResponseAsync(connection, "a5").ConfigureAwait(false);
            Assert.IsTrue(capability[^1].StartsWith("a5 OK", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapThreadFailsClosedWhenWorkerIsUnavailableOrMalformed()
    {
        foreach (var malformed in new[] { false, true })
        {
            var port = ReservePort();
            var environment = CreateEnvironment(imapPort: port);

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var server = (await ServerFixture.StartImapAsync(
                environment, port,
                applicationService: new UnavailableImapMailboxApplicationService(
                    listingUnavailable: false,
                    selectionAvailable: true,
                    threadMalformed: malformed)).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var serverLifetime = server.ConfigureAwait(false);

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var connectionLifetime = connection.ConfigureAwait(false);

            await connection.ReadLineAsync().ConfigureAwait(false);
            await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
            await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
            var selected = await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);
            Assert.IsTrue(selected[^1].StartsWith("a3 OK", StringComparison.Ordinal));

            await connection.WriteLineAsync("a4 THREAD REFERENCES US-ASCII ALL").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
                "a4 NO [UNAVAILABLE]", StringComparison.Ordinal));
            await connection.WriteLineAsync("a5 CAPABILITY").ConfigureAwait(false);
            var capability = await ReadUntilTaggedResponseAsync(connection, "a5").ConfigureAwait(false);
            Assert.IsTrue(capability[^1].StartsWith("a5 OK", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapFetchSeenUpdateFailsClosedWhenWorkerIsUnavailableOrMalformed()
    {
        foreach (var malformed in new[] { false, true })
        {
            var port = ReservePort();
            var environment = CreateEnvironment(imapPort: port);
            var application = new UnavailableImapMailboxApplicationService(
                listingUnavailable: false,
                selectionAvailable: true,
                seenMalformed: malformed)
            {
                SelectedFolderId = Guid.CreateVersion7(),
            };

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var server = (await ServerFixture.StartImapAsync(
                environment, port, applicationService: application).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var serverLifetime = server.ConfigureAwait(false);

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var connectionLifetime = connection.ConfigureAwait(false);

            await connection.ReadLineAsync().ConfigureAwait(false);
            await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
            await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
            var selected = await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);
            Assert.IsTrue(selected[^1].StartsWith("a3 OK", StringComparison.Ordinal));

            await connection.WriteLineAsync("a4 FETCH 1 BODY[TEXT]").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
                "a4 NO [UNAVAILABLE]", StringComparison.Ordinal));
            await connection.WriteLineAsync("a5 CAPABILITY").ConfigureAwait(false);
            var capability = await ReadUntilTaggedResponseAsync(connection, "a5").ConfigureAwait(false);
            Assert.IsTrue(capability[^1].StartsWith("a5 OK", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapFetchPageFailsClosedWhenWorkerIsUnavailableOrMalformed()
    {
        foreach (var malformed in new[] { false, true })
        {
            var port = ReservePort();
            var environment = CreateEnvironment(imapPort: port);

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var server = (await ServerFixture.StartImapAsync(
                environment, port,
                applicationService: new UnavailableImapMailboxApplicationService(
                    listingUnavailable: false,
                    selectionAvailable: true,
                    fetchMalformed: malformed)).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var serverLifetime = server.ConfigureAwait(false);

            // The immediately following configured await-using owns this resource for the entire loop iteration, including exception paths.
#pragma warning disable CA2000
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));

#pragma warning restore CA2000

            await using var connectionLifetime = connection.ConfigureAwait(false);

            await connection.ReadLineAsync().ConfigureAwait(false);
            await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
            await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
            var selected = await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);
            Assert.IsTrue(selected[^1].StartsWith("a3 OK", StringComparison.Ordinal));

            await connection.WriteLineAsync("a4 UID FETCH 1 FLAGS").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
                "a4 NO [UNAVAILABLE]", StringComparison.Ordinal));
            await connection.WriteLineAsync("a5 CAPABILITY").ConfigureAwait(false);
            var capability = await ReadUntilTaggedResponseAsync(connection, "a5").ConfigureAwait(false);
            Assert.IsTrue(capability[^1].StartsWith("a5 OK", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapFetchRendersPagedWorkerMessagesWithoutLocalMailboxRows()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var application = new UnavailableImapMailboxApplicationService(
            listingUnavailable: false,
            selectionAvailable: true,
            fetchPaged: true)
        {
            SelectedFolderId = Guid.CreateVersion7(),
        };
        var server = (await ServerFixture.StartImapAsync(
            environment, port, applicationService: application).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
        var selected = await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);
        Assert.IsTrue(selected[^1].StartsWith("a3 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 UID FETCH 1:* (UID FLAGS)").ConfigureAwait(false);
        Assert.AreEqual("* 1 FETCH (FLAGS () UID 1)", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.AreEqual("* 2 FETCH (FLAGS () UID 2)", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));
        CollectionAssert.AreEqual(ExpectedVector3, application.FetchCursors);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapFetchRejectsChangedMailboxSnapshotBeforeSecondPage()
    {
        var port = ReservePort();
        var application = new UnavailableImapMailboxApplicationService(
            listingUnavailable: false,
            selectionAvailable: true,
            fetchPaged: true,
            fetchDrift: true)
        {
            SelectedFolderId = Guid.CreateVersion7(),
        };
        var server = (await ServerFixture.StartImapAsync(
            CreateEnvironment(imapPort: port), port, applicationService: application).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX").ConfigureAwait(false);
        var selected = await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);
        Assert.IsTrue(selected[^1].StartsWith("a3 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 UID FETCH 1:* (UID FLAGS)").ConfigureAwait(false);
        Assert.AreEqual("* 1 FETCH (FLAGS () UID 1)", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith(
            "a4 NO [UNAVAILABLE]", StringComparison.Ordinal));
        CollectionAssert.AreEqual(ExpectedVector3, application.FetchCursors);
    }

    [TestMethod]
    [Timeout(15_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapBoundsConcurrentMessageWrites scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapBoundsConcurrentMessageWrites()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedSentMessagesWithReverseDatesAsync().ConfigureAwait(false);
        const string message =
            "From: user@tenant.example.test\r\n" +
            "To: user@tenant.example.test\r\n" +
            "Subject: bounded append\r\n\r\n" +
            "body\r\n";
        var connections = new List<ProtocolConnection>();

        try
        {
            for (var index = 0; index < 3; index++)
            {
                var connection = await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false);
                connections.Add(connection);
                await connection.ReadLineAsync().ConfigureAwait(false);
                await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
                Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
                await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
                await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
                Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
                await connection.WriteLineAsync("a3 SELECT Sent").ConfigureAwait(false);

                string response;
                do
                {
                    response = await connection.ReadLineAsync().ConfigureAwait(false);
                }
                while (!response.StartsWith("a3 ", StringComparison.Ordinal));

                Assert.IsTrue(response.StartsWith("a3 OK", StringComparison.Ordinal));
            }

            for (var index = 0; index < 2; index++)
            {
                await connections[index].WriteLineAsync($"a4 APPEND \"Sent\" {{{message.Length}}}").ConfigureAwait(false);
                Assert.IsTrue((await connections[index].ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
            }

            await connections[2].WriteLineAsync("a4 COPY 1 Trash").ConfigureAwait(false);
            Assert.IsTrue((await connections[2].ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 NO [UNAVAILABLE]", StringComparison.Ordinal));
            await connections[2].WriteLineAsync($"a5 APPEND \"Sent\" {{{message.Length}}}").ConfigureAwait(false);
            Assert.IsTrue((await connections[2].ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 NO [UNAVAILABLE]", StringComparison.Ordinal));

            await connections[0].WriteRawAsync(message).ConfigureAwait(false);
            await connections[0].WriteLineAsync(string.Empty).ConfigureAwait(false);
            Assert.IsTrue((await connections[0].ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK [APPENDUID", StringComparison.Ordinal));

            var accepted = false;
            for (var attempt = 0; attempt < 10 && !accepted; attempt++)
            {
                await connections[2].WriteLineAsync($"a6{attempt} APPEND \"Sent\" {{{message.Length}}}").ConfigureAwait(false);
                var response = await connections[2].ReadLineAsync().ConfigureAwait(false);
                accepted = response.StartsWith("+ ", StringComparison.Ordinal);
                if (!accepted)
                {
                    Assert.IsTrue(response.Contains("NO [UNAVAILABLE]", StringComparison.Ordinal));
                    await Task.Delay(20).ConfigureAwait(false);
                }
            }

            Assert.IsTrue(accepted);
        }
        finally
        {
            foreach (var connection in connections)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapSequenceNumbersFollowUidOrder()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedSentMessagesWithReverseDatesAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 SELECT Sent").ConfigureAwait(false);
        string line;
        do
        {
            line = await connection.ReadLineAsync().ConfigureAwait(false);
        }
        while (!line.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 FETCH 1:* (UID)").ConfigureAwait(false);
        var responses = new List<string>();
        do
        {
            line = await connection.ReadLineAsync().ConfigureAwait(false);
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
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedSentMessagesWithReverseDatesAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT Sent").ConfigureAwait(false);

        string line;
        do
        {
            line = await connection.ReadLineAsync().ConfigureAwait(false);
        }
        while (!line.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 UID FETCH 2 (UID BODY.PEEK[TEXT])").ConfigureAwait(false);
        var peekResponse = new List<string>();
        do
        {
            line = await connection.ReadLineAsync().ConfigureAwait(false);
            peekResponse.Add(line);
        }
        while (!line.StartsWith("a4 ", StringComparison.Ordinal));

        Assert.IsTrue(peekResponse[0].StartsWith("* 2 FETCH", StringComparison.Ordinal));
        Assert.IsTrue(peekResponse.Any(value => value.Contains("BODY[TEXT] {6}", StringComparison.Ordinal)));
        Assert.IsTrue(peekResponse.Any(value => string.Equals(value, "body", StringComparison.Ordinal)));
        Assert.IsFalse((await server.GetStoredEmailByUidAsync(2).ConfigureAwait(false)).IsRead);

        await connection.WriteLineAsync("a5 UID FETCH 2 (UID BODY[TEXT] MODSEQ)").ConfigureAwait(false);
        do
        {
            line = await connection.ReadLineAsync().ConfigureAwait(false);
        }
        while (!line.StartsWith("a5 ", StringComparison.Ordinal));

        var stored = await server.GetStoredEmailByUidAsync(2).ConfigureAwait(false);
        Assert.IsTrue(stored.IsRead);
        Assert.AreEqual(3, stored.ModSeq);
    }

    [TestMethod]
    [Timeout(15_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapFetchProjectsMultipartBodyStructureAndNestedSections scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapFetchProjectsMultipartBodyStructureAndNestedSections()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
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

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync($"a3 APPEND Sent {{{message.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 SELECT Sent").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a4").ConfigureAwait(false);

        await connection.WriteLineAsync("a5 UID FETCH 1 (UID BODYSTRUCTURE)").ConfigureAwait(false);
        var bodyStructure = await connection.ReadLineAsync().ConfigureAwait(false);
        StringAssert.Contains(bodyStructure, "BODYSTRUCTURE", StringComparison.Ordinal);
        StringAssert.Contains(bodyStructure, "\"ALTERNATIVE\"", StringComparison.Ordinal);
        StringAssert.Contains(bodyStructure, "\"MIXED\"", StringComparison.Ordinal);
        StringAssert.Contains(bodyStructure, "\"TEXT\" \"HTML\"", StringComparison.Ordinal);
        StringAssert.Contains(bodyStructure, "\"APPLICATION\" \"OCTET-STREAM\"", StringComparison.Ordinal);
        StringAssert.Contains(bodyStructure, "\"BASE64\"", StringComparison.Ordinal);
        StringAssert.Contains(bodyStructure, "(\"BOUNDARY\" \"mix\")", StringComparison.Ordinal);
        StringAssert.Contains(bodyStructure, "\"<attachment@example.net>\"", StringComparison.Ordinal);
        StringAssert.Contains(bodyStructure, "\"dGVzdC1kaWdlc3Q=\"", StringComparison.Ordinal);
        StringAssert.Contains(bodyStructure, "(\"ATTACHMENT\" (\"FILENAME\" \"test.txt\"))", StringComparison.Ordinal);
        StringAssert.Contains(bodyStructure, "(\"en\" \"fr\")", StringComparison.Ordinal);
        StringAssert.Contains(bodyStructure, "\"files/test.txt\"", StringComparison.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 UID FETCH 1 (UID BODY)").ConfigureAwait(false);
        var body = await connection.ReadLineAsync().ConfigureAwait(false);
        StringAssert.Contains(body, "BODY", StringComparison.Ordinal);
        StringAssert.Contains(body, "\"<attachment@example.net>\"", StringComparison.Ordinal);
        Assert.IsFalse(body.Contains("\"BOUNDARY\"", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("\"ATTACHMENT\"", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("dGVzdC1kaWdlc3Q=", StringComparison.Ordinal));
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 UID FETCH 1 (UID BODY.PEEK[1.2])").ConfigureAwait(false);
        var htmlSection = string.Join('\n', await ReadUntilTaggedResponseAsync(connection, "a7").ConfigureAwait(false));
        StringAssert.Contains(htmlSection, "BODY[1.2]", StringComparison.Ordinal);
        StringAssert.Contains(htmlSection, "<html><body>html=20body</body></html>", StringComparison.Ordinal);
        Assert.IsFalse((await server.GetStoredEmailByUidAsync(1).ConfigureAwait(false)).IsRead);

        await connection.WriteLineAsync("a8 UID FETCH 1 (UID BODY.PEEK[2.MIME])").ConfigureAwait(false);
        var attachmentHeaders = string.Join('\n', await ReadUntilTaggedResponseAsync(connection, "a8").ConfigureAwait(false));
        StringAssert.Contains(attachmentHeaders, "BODY[2.MIME]", StringComparison.Ordinal);
        StringAssert.Contains(attachmentHeaders, "Content-Type: application/octet-stream", StringComparison.Ordinal);
        StringAssert.Contains(attachmentHeaders, "Content-Disposition: attachment", StringComparison.Ordinal);
        Assert.IsFalse((await server.GetStoredEmailByUidAsync(1).ConfigureAwait(false)).IsRead);

        await connection.WriteLineAsync("a9 UID FETCH 1 (UID BODY[2])").ConfigureAwait(false);
        var attachment = string.Join('\n', await ReadUntilTaggedResponseAsync(connection, "a9").ConfigureAwait(false));
        StringAssert.Contains(attachment, "BODY[2]", StringComparison.Ordinal);
        StringAssert.Contains(attachment, "YXR0YWNobWVudA==", StringComparison.Ordinal);
        Assert.IsTrue((await server.GetStoredEmailByUidAsync(1).ConfigureAwait(false)).IsRead);
    }

    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapBinaryDecodesMimeSectionsAndSupportsLiteral8Append scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapBinaryDecodesMimeSectionsAndSupportsLiteral8Append()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        const string message =
            "From: sender@example.net\r\n" +
            $"To: {TestUsername}\r\n" +
            "Subject: binary sections\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=bin\r\n\r\n" +
            "--bin\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n\r\n" +
            "first=0Asecond=0Dthird\r\n" +
            "--bin\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\n" +
            "QQD/Cg==\r\n" +
            "--bin\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Content-Transfer-Encoding: x-rot13\r\n\r\n" +
            "uryyb\r\n" +
            "--bin--\r\n";

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync($"a3 APPEND Sent {{{message.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));
        await connection.WriteLineAsync("a4 SELECT Sent").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a4").ConfigureAwait(false);

        await connection.WriteLineAsync(
            "a4h UID FETCH 1 (UID BODY.PEEK[HEADER.FIELDS (SUBJECT FROM)])").ConfigureAwait(false);
        var headerFields = string.Join('\n', await ReadUntilTaggedResponseAsync(connection, "a4h").ConfigureAwait(false));
        StringAssert.Contains(headerFields, "BODY[HEADER.FIELDS (SUBJECT FROM)]", StringComparison.Ordinal);
        StringAssert.Contains(headerFields, "Subject: binary sections", StringComparison.Ordinal);
        StringAssert.Contains(headerFields, "From: sender@example.net", StringComparison.Ordinal);
        Assert.IsFalse((await server.GetStoredEmailByUidAsync(1).ConfigureAwait(false)).IsRead);

        await connection.WriteLineAsync(
            "a5 UID FETCH 1 (UID FLAGS BINARY.PEEK[1]<7.6> BINARY.SIZE[1] BINARY.SIZE[2])").ConfigureAwait(false);
        var response = await connection.ReadLineAsync().ConfigureAwait(false);
        StringAssert.Contains(response, "* 1 FETCH (FLAGS () BINARY[1]<7> {6}", StringComparison.Ordinal);
        Assert.AreEqual("second", await connection.ReadCharactersAsync(6).ConfigureAwait(false), StringComparer.Ordinal);
        response = await connection.ReadLineAsync().ConfigureAwait(false);
        StringAssert.Contains(response, "BINARY.SIZE[1] 20", StringComparison.Ordinal);
        StringAssert.Contains(response, "BINARY.SIZE[2] 4", StringComparison.Ordinal);
        StringAssert.Contains(response, "UID 1)", StringComparison.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK", StringComparison.Ordinal));
        Assert.IsFalse((await server.GetStoredEmailByUidAsync(1).ConfigureAwait(false)).IsRead);

        await connection.WriteLineAsync("a6 UID FETCH 1 (UID BINARY[2])").ConfigureAwait(false);
        response = await connection.ReadLineAsync().ConfigureAwait(false);
        StringAssert.Contains(response, "* 1 FETCH (BINARY[2] ~{4}", StringComparison.Ordinal);
        CollectionAssert.AreEqual(
            new byte[] { 0x41, 0x00, 0xff, 0x0a },
            Encoding.Latin1.GetBytes(await connection.ReadCharactersAsync(4).ConfigureAwait(false)));
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "UID 1)", StringComparison.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK", StringComparison.Ordinal));
        Assert.IsTrue((await server.GetStoredEmailByUidAsync(1).ConfigureAwait(false)).IsRead);

        await connection.WriteLineAsync("a7 STORE 1 -FLAGS.SILENT (\\Seen)").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync(
            "a8 FETCH 1 (BINARY.PEEK[1]<0.5> BINARY.PEEK[2]<0.1>)").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "BINARY[1]<0> {5}", StringComparison.Ordinal);
        Assert.AreEqual("first", await connection.ReadCharactersAsync(5).ConfigureAwait(false), StringComparer.Ordinal);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "BINARY[2]<0> {1}", StringComparison.Ordinal);
        Assert.AreEqual("A", await connection.ReadCharactersAsync(1).ConfigureAwait(false), StringComparer.Ordinal);
        Assert.AreEqual(")", (await connection.ReadLineAsync().ConfigureAwait(false)).Trim(), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a8 OK", StringComparison.Ordinal));
        Assert.IsFalse((await server.GetStoredEmailByUidAsync(1).ConfigureAwait(false)).IsRead);

        await connection.WriteLineAsync("a9 UID FETCH 1 BINARY.SIZE[3]").ConfigureAwait(false);
        response = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.IsTrue(response.StartsWith("a9 NO [UNKNOWN-CTE]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a10 UID FETCH 1 BINARY[]").ConfigureAwait(false);
        response = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.IsTrue(response.StartsWith("a10 NO [CANNOT]", StringComparison.Ordinal));
        await connection.WriteLineAsync("a11 UID FETCH 1 BINARY[01]").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a11 BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a11b UID FETCH 1 BINARY[1]<0.0>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a11b BAD", StringComparison.Ordinal));
        await connection.WriteLineAsync("a12 NOOP").ConfigureAwait(false);
        Assert.AreEqual("a12 OK NOOP completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);

        const string literal8Message =
            "From: sender@example.net\r\n" +
            $"To: {TestUsername}\r\n" +
            "Subject: literal8\r\n\r\n" +
            "eight bit body: \u00ff\r\n";
        await connection.WriteRawAsync(
            $"a13 APPEND Sent ~{{{literal8Message.Length}+}}\r\n{literal8Message}\r\n").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a13 OK [APPENDUID", StringComparison.Ordinal));

        const string unsupportedBinaryMessage =
            "From: sender@example.net\r\n" +
            $"To: {TestUsername}\r\n" +
            "Subject: binary append\r\n\r\n" +
            "A\0B";
        await connection.WriteLineAsync(
            $"a14 APPEND Sent ~{{{unsupportedBinaryMessage.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(unsupportedBinaryMessage).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        response = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.IsTrue(response.StartsWith("a14 NO [UNKNOWN-CTE]", StringComparison.Ordinal));
        Assert.AreEqual(2, await server.CountStoredEmailsAsync().ConfigureAwait(false));
        await connection.WriteLineAsync("a15 NOOP").ConfigureAwait(false);
        Assert.AreEqual("a15 OK NOOP completed", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
    }

    [TestMethod]
    [Timeout(30_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImapObjectIdsRemainStableAcrossRenameCopyMoveAndSearch scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImapObjectIdsRemainStableAcrossRenameCopyMoveAndSearch()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        const string rootMessage =
            "From: sender@example.net\r\n" +
            $"To: {TestUsername}\r\n" +
            "Subject: object root\r\n" +
            "Message-ID: <object-root@example.net>\r\n\r\n" +
            "root body\r\n";
        const string replyMessage =
            "From: sender@example.net\r\n" +
            $"To: {TestUsername}\r\n" +
            "Subject: Re: object root\r\n" +
            "Message-ID: <object-reply@example.net>\r\n" +
            "In-Reply-To: <object-root@example.net>\r\n" +
            "References: <object-root@example.net>\r\n\r\n" +
            "reply body\r\n";

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 CREATE ObjectBox").ConfigureAwait(false);
        var response = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.IsTrue(response.StartsWith("a3 OK [MAILBOXID (", StringComparison.Ordinal));
        var mailboxId = ExtractObjectId(response, "MAILBOXID");
        AssertObjectId(mailboxId, 'F');

        await connection.WriteLineAsync("a4 STATUS ObjectBox (MAILBOXID)").ConfigureAwait(false);
        response = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.AreEqual(mailboxId, ExtractObjectId(response, "MAILBOXID"), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a5 LIST \"\" \"ObjectBox\" RETURN (STATUS (MAILBOXID))").ConfigureAwait(false);
        var responses = await ReadUntilTaggedResponseAsync(connection, "a5").ConfigureAwait(false);
        var listStatus = responses.Single(line => line.StartsWith("* STATUS", StringComparison.Ordinal));
        Assert.AreEqual(mailboxId, ExtractObjectId(listStatus, "MAILBOXID"), StringComparer.Ordinal);

        await connection.WriteLineAsync("a6 EXAMINE ObjectBox").ConfigureAwait(false);
        responses = await ReadUntilTaggedResponseAsync(connection, "a6").ConfigureAwait(false);
        var selectMailboxId = responses.Single(line =>
            line.StartsWith("* OK [MAILBOXID", StringComparison.Ordinal));
        Assert.AreEqual(mailboxId, ExtractObjectId(selectMailboxId, "MAILBOXID"), StringComparer.Ordinal);
        Assert.IsTrue(responses[^1].StartsWith("a6 OK [READ-ONLY]", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 RENAME ObjectBox ObjectBoxRenamed").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a8 STATUS ObjectBoxRenamed (MAILBOXID)").ConfigureAwait(false);
        response = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.AreEqual(mailboxId, ExtractObjectId(response, "MAILBOXID"), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a8 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a9 CREATE RecreatedBox").ConfigureAwait(false);
        var firstCreatedId = ExtractObjectId(await connection.ReadLineAsync().ConfigureAwait(false), "MAILBOXID");
        await connection.WriteLineAsync("a10 DELETE RecreatedBox").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a10 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a11 CREATE RecreatedBox").ConfigureAwait(false);
        var secondCreatedId = ExtractObjectId(await connection.ReadLineAsync().ConfigureAwait(false), "MAILBOXID");
        AssertObjectId(firstCreatedId, 'F');
        AssertObjectId(secondCreatedId, 'F');
        Assert.AreNotEqual(firstCreatedId, secondCreatedId, StringComparer.Ordinal);

        await connection.WriteLineAsync($"a12 APPEND Sent {{{rootMessage.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(rootMessage).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a12 OK [APPENDUID", StringComparison.Ordinal));
        await connection.WriteLineAsync($"a13 APPEND Sent {{{replyMessage.Length}}}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(replyMessage).ConfigureAwait(false);
        await connection.WriteLineAsync(string.Empty).ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a13 OK [APPENDUID", StringComparison.Ordinal));

        await connection.WriteLineAsync("a14 SELECT Sent").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a14").ConfigureAwait(false);
        await connection.WriteLineAsync("a15 UID FETCH 1:* (UID EMAILID THREADID)").ConfigureAwait(false);
        responses = await ReadUntilTaggedResponseAsync(connection, "a15").ConfigureAwait(false);
        var sourceFetches = responses
            .Where(line => line.StartsWith("* ", StringComparison.Ordinal)
                && line.Contains(" FETCH ", StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(2, sourceFetches.Length);
        StringAssert.Contains(sourceFetches[0], "UID 1", StringComparison.Ordinal);
        StringAssert.Contains(sourceFetches[1], "UID 2", StringComparison.Ordinal);
        var rootEmailId = ExtractObjectId(sourceFetches[0], "EMAILID");
        var replyEmailId = ExtractObjectId(sourceFetches[1], "EMAILID");
        var rootThreadId = ExtractObjectId(sourceFetches[0], "THREADID");
        var replyThreadId = ExtractObjectId(sourceFetches[1], "THREADID");
        AssertObjectId(rootEmailId, 'M');
        AssertObjectId(replyEmailId, 'M');
        AssertObjectId(rootThreadId, 'T');
        AssertObjectId(replyThreadId, 'T');
        Assert.AreNotEqual(rootEmailId, replyEmailId, StringComparer.Ordinal);
        Assert.AreEqual(rootThreadId, replyThreadId, StringComparer.Ordinal);
        Assert.AreNotEqual(rootEmailId, rootThreadId, StringComparer.Ordinal);

        await connection.WriteLineAsync($"a16 UID SEARCH EMAILID {rootEmailId}").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a16 OK", StringComparison.Ordinal));
        var wrongCaseEmailId = char.ToLowerInvariant(rootEmailId[0]) + rootEmailId[1..];
        await connection.WriteLineAsync($"a17 UID SEARCH EMAILID {wrongCaseEmailId}").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a17 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync($"a18 UID SEARCH THREADID {rootThreadId}").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1 2", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a18 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a19 UID SEARCH EMAILID M!").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a19 BAD", StringComparison.Ordinal));

        await connection.WriteLineAsync(
            "a20 UID FETCH 1 BODY.PEEK[HEADER.FIELDS (EMAILID THREADID)]").ConfigureAwait(false);
        var headerOnlyFetch = string.Join('\n', await ReadUntilTaggedResponseAsync(connection, "a20").ConfigureAwait(false));
        Assert.IsFalse(headerOnlyFetch.Contains(" EMAILID (", StringComparison.Ordinal));
        Assert.IsFalse(headerOnlyFetch.Contains(" THREADID (", StringComparison.Ordinal));

        await connection.WriteLineAsync("a21 UID COPY 1 ObjectBoxRenamed").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a21 OK [COPYUID", StringComparison.Ordinal));
        await connection.WriteLineAsync("a22 UID MOVE 2 ObjectBoxRenamed").ConfigureAwait(false);
        responses = await ReadUntilTaggedResponseAsync(connection, "a22").ConfigureAwait(false);
        Assert.IsTrue(responses[^1].StartsWith("a22 OK [COPYUID", StringComparison.Ordinal));

        await connection.WriteLineAsync("a23 SELECT ObjectBoxRenamed").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a23").ConfigureAwait(false);
        await connection.WriteLineAsync("a24 UID FETCH 1:* (UID EMAILID THREADID)").ConfigureAwait(false);
        responses = await ReadUntilTaggedResponseAsync(connection, "a24").ConfigureAwait(false);
        var destinationFetches = responses
            .Where(line => line.StartsWith("* ", StringComparison.Ordinal)
                && line.Contains(" FETCH ", StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(2, destinationFetches.Length);
        Assert.AreEqual(rootEmailId, ExtractObjectId(destinationFetches[0], "EMAILID"), StringComparer.Ordinal);
        Assert.AreEqual(replyEmailId, ExtractObjectId(destinationFetches[1], "EMAILID"), StringComparer.Ordinal);
        Assert.AreEqual(rootThreadId, ExtractObjectId(destinationFetches[0], "THREADID"), StringComparer.Ordinal);
        Assert.AreEqual(replyThreadId, ExtractObjectId(destinationFetches[1], "THREADID"), StringComparer.Ordinal);

        await connection.WriteLineAsync(
            $"a25 UID SEARCH OR EMAILID {rootEmailId} EMAILID {replyEmailId}").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1 2", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a25 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapMutationsUseUidSequenceOrderWithoutChangingMessageContent()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedSentMessagesWithReverseDatesAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT Sent").ConfigureAwait(false);

        string line;
        do
        {
            line = await connection.ReadLineAsync().ConfigureAwait(false);
        }
        while (!line.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 SEARCH HEADER X-Test-Uid 1").ConfigureAwait(false);
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 STORE 1 +FLAGS.SILENT (\\Deleted)").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a6 EXPUNGE").ConfigureAwait(false);
        Assert.AreEqual("* 1 EXPUNGE", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a7 MOVE 1 Trash").ConfigureAwait(false);
        Assert.AreEqual("* 1 EXPUNGE", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK [COPYUID", StringComparison.Ordinal));

        var moved = await server.GetStoredEmailAsync(DefaultFolders.Trash).ConfigureAwait(false);
        Assert.AreEqual("UID 2", moved.Subject, StringComparer.Ordinal);
        Assert.AreEqual("body\r\n", moved.Body, StringComparer.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapCopyChecksUserQuotaAndPreservesSelectedContent()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var sourceSize = await server.SeedSentMessagesWithReverseDatesAsync().ConfigureAwait(false);
        await server.SetUserQuotaAsync(3 * sourceSize - 1).ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT Sent").ConfigureAwait(false);

        string response;
        do
        {
            response = await connection.ReadLineAsync().ConfigureAwait(false);
        }
        while (!response.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 COPY 1 Trash").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a4 NO [OVERQUOTA]", StringComparison.Ordinal));
        Assert.AreEqual(2, await server.CountStoredEmailsAsync().ConfigureAwait(false));

        await server.SetUserQuotaAsync(3 * sourceSize).ConfigureAwait(false);
        await connection.WriteLineAsync("a5 COPY 1 Trash").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK [COPYUID", StringComparison.Ordinal));
        Assert.AreEqual(3, await server.CountStoredEmailsAsync().ConfigureAwait(false));

        await server.SetUserQuotaAsync(4 * sourceSize - 1).ConfigureAwait(false);
        await connection.WriteLineAsync("a6 UID COPY 2 Trash").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 NO [OVERQUOTA]", StringComparison.Ordinal));
        Assert.AreEqual(3, await server.CountStoredEmailsAsync().ConfigureAwait(false));

        await server.SetUserQuotaAsync(4 * sourceSize).ConfigureAwait(false);
        await connection.WriteLineAsync("a7 UID COPY 2 Trash").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a7 OK [COPYUID", StringComparison.Ordinal));

        var copies = await server.GetStoredEmailsAsync(DefaultFolders.Trash).ConfigureAwait(false);
        CollectionAssert.AreEqual(ExpectedVector4, copies.Select(email => email.Subject).ToArray());
        Assert.IsTrue(copies.All(email => string.Equals(email.Body, "body\r\n", StringComparison.Ordinal)));
        Assert.AreEqual(4, await server.CountStoredEmailsAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapMovePersistsQresyncTombstones()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedSentMessagesWithReverseDatesAsync().ConfigureAwait(false);

        {
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
            await using var connectionLifetime = connection.ConfigureAwait(false);
            await connection.ReadLineAsync().ConfigureAwait(false);
            await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
            await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync("a3 ENABLE QRESYNC").ConfigureAwait(false);
            Assert.AreEqual("* ENABLED QRESYNC", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a3 OK", StringComparison.Ordinal));
            await connection.WriteLineAsync("a4 SELECT Sent").ConfigureAwait(false);
            await ReadUntilTaggedResponseAsync(connection, "a4").ConfigureAwait(false);

            await connection.WriteLineAsync("a5 MOVE 1 Trash").ConfigureAwait(false);
            Assert.AreEqual("* VANISHED 1", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a5 OK [COPYUID", StringComparison.Ordinal));

            await connection.WriteLineAsync("a6 UID MOVE 2 Trash").ConfigureAwait(false);
            Assert.AreEqual("* VANISHED 2", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a6 OK [COPYUID", StringComparison.Ordinal));
        }

        var reconnect = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var reconnectLifetime = reconnect.ConfigureAwait(false);
        await reconnect.ReadLineAsync().ConfigureAwait(false);
        await reconnect.WriteLineAsync("b1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await reconnect.ReadLineAsync().ConfigureAwait(false)).StartsWith("b1 OK", StringComparison.Ordinal));
        await reconnect.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await reconnect.WriteLineAsync($"b2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await reconnect.ReadLineAsync().ConfigureAwait(false)).StartsWith("b2 OK", StringComparison.Ordinal));
        await reconnect.WriteLineAsync("b3 ENABLE QRESYNC").ConfigureAwait(false);
        Assert.AreEqual("* ENABLED QRESYNC", await reconnect.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await reconnect.ReadLineAsync().ConfigureAwait(false)).StartsWith("b3 OK", StringComparison.Ordinal));
        await reconnect.WriteLineAsync("b4 SELECT Sent (QRESYNC (1 2 1:2))").ConfigureAwait(false);
        var responses = await ReadUntilTaggedResponseAsync(reconnect, "b4").ConfigureAwait(false);

        Assert.IsTrue(responses.Contains("* VANISHED (EARLIER) 1:2"));
        Assert.IsTrue(responses.Contains("* OK [HIGHESTMODSEQ 4]"));
        Assert.AreEqual(2, (await server.GetStoredEmailsAsync(DefaultFolders.Trash).ConfigureAwait(false)).Count);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapRejectsMalformedMessageSetsWithoutDisconnecting()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        var server = (await ServerFixture.StartImapAsync(environment, port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.SeedSentMessagesWithReverseDatesAsync().ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("a1 STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT Sent").ConfigureAwait(false);
        await ReadUntilTaggedResponseAsync(connection, "a3").ConfigureAwait(false);

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
            await connection.WriteLineAsync(command).ConfigureAwait(false);
            var tag = command[..command.IndexOf(' ', StringComparison.Ordinal)];
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith($"{tag} BAD", StringComparison.Ordinal));
        }

        await connection.WriteLineAsync("a9 STORE 1:2147483647 +FLAGS.SILENT (\\Seen)").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a9 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a10 NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("a10 OK", StringComparison.Ordinal));
        Assert.AreEqual(2, await server.CountStoredEmailsAsync().ConfigureAwait(false));
        Assert.AreEqual(0, (await server.GetStoredEmailsAsync(DefaultFolders.Trash).ConfigureAwait(false)).Count);
    }

    private static async Task<List<string>> ReadUntilTaggedResponseAsync(
        ProtocolConnection connection,
        string tag)
    {
        var responses = new List<string>();
        string response;
        do
        {
            response = await connection.ReadLineAsync().ConfigureAwait(false);
            responses.Add(response);
        }
        while (!response.StartsWith($"{tag} ", StringComparison.Ordinal));

        return responses;
    }

    private static string ExtractObjectId(string response, string dataItem)
    {
        var marker = $"{dataItem} (";
        var start = response.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"{dataItem} was missing from: {response}");
        start += marker.Length;
        var end = response.IndexOf(')', start);
        Assert.IsTrue(end > start, $"{dataItem} was malformed in: {response}");
        return response[start..end];
    }

    private static void AssertObjectId(string objectId, char expectedPrefix)
    {
        Assert.IsTrue(objectId.Length is >= 1 and <= 255);
        Assert.AreEqual(expectedPrefix, objectId[0]);
        Assert.IsTrue(objectId.All(character =>
            character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_'
                or '-'));
    }

    private static async Task<List<string>> ReadPop3MultilineAsync(ProtocolConnection connection)
    {
        var response = new List<string>();
        while (true)
        {
            var line = await connection.ReadLineAsync().ConfigureAwait(false);
            if (string.Equals(line, ".", StringComparison.Ordinal))
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
        bool enableOAuth = false,
        bool requireAuthentication = true)
    {
        return new EnvironmentConfig
        {
            Smtp = new SmtpConfig
            {
                Hostname = "email.tenant.example.test",
                Port = smtpPort ?? ReservePort(),
                SubmissionPort = submissionPort ?? ReservePort(),
                ImplicitTlsPort = smtpImplicitTlsPort ?? ReservePort(),
                EnableSmtp = smtpPort.HasValue,
                EnableSubmission = submissionPort.HasValue,
                EnableImplicitTls = smtpImplicitTlsPort.HasValue,
                EnableStartTls = true,
                RequireAuth = requireAuthentication,
                AllowRelay = requireAuthentication,
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
                PublicBaseUrl = "https://email.tenant.example.test",
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
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task TriggerTlsPeerFailureAsync(int port, bool sendMalformedPayload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
        if (sendMalformedPayload)
        {
            var payload = "GET / HTTP/1.0\r\n\r\n"u8.ToArray();
            await client.GetStream().WriteAsync(payload, timeout.Token).ConfigureAwait(false);
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
            await Task.Delay(20).ConfigureAwait(false);

        Assert.IsTrue(condition(), failureMessage);
    }

    private static async Task AuthenticateSmtpAsync(ProtocolConnection connection)
    {
        await UpgradeSmtpToTlsAsync(connection).ConfigureAwait(false);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
        await connection.WriteLineAsync($"AUTH PLAIN {credentials}").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("235 ", StringComparison.Ordinal));
    }

    private static string CreateXOAuth2Response(string accessToken) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"user={TestUsername}\u0001auth=Bearer {accessToken}\u0001\u0001"));

    private static async Task BeginInboundMessageAsync(ProtocolConnection connection)
    {
        await BeginInboundEnvelopeAsync(connection).ConfigureAwait(false);
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("354 ", StringComparison.Ordinal));
    }

    private static async Task BeginInboundEnvelopeAsync(ProtocolConnection connection)
    {
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@tenant.example.test>").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("250 ", StringComparison.Ordinal));
    }

    private static async Task UpgradeSmtpToTlsAsync(ProtocolConnection connection)
    {
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("220 ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
    }

    private sealed class UnavailablePop3ApplicationService : IPop3ApplicationService
    {
        public Task<Pop3IdentityResult> AuthenticatePasswordAsync(
            Pop3PasswordAuthentication request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<Pop3IdentityResult>(new IOException("Worker unavailable"));

        public Task<Pop3IdentityResult> AuthenticateOAuthAsync(
            Pop3OAuthAuthentication request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<Pop3IdentityResult>(new IOException("Worker unavailable"));

        public Task<Pop3MaildropSnapshot> ListMaildropAsync(
            Pop3UserRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Authentication did not succeed.");

        public Task<Pop3MessageResult> GetMessageAsync(
            Pop3MessageRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Authentication did not succeed.");

        public Task<Pop3DeleteResult> CommitDeletesAsync(
            Pop3DeleteRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Authentication did not succeed.");
    }

    private sealed class UnavailableImapApplicationService : IImapApplicationService
    {
        public Task<ImapIdentityResult> AuthenticatePasswordAsync(
            ImapPasswordAuthentication request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapIdentityResult>(new IOException("Worker unavailable"));

        public Task<ImapIdentityResult> AuthenticateOAuthAsync(
            ImapOAuthAuthentication request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapIdentityResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxListResult> ListMailboxesAsync(
            ImapMailboxListRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxListResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxStatusResult> GetMailboxStatusesAsync(
            ImapMailboxStatusRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxStatusResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxSubscriptionResult> SetMailboxSubscriptionAsync(
            ImapMailboxSubscriptionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxSubscriptionResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxCreateResult> CreateMailboxAsync(
            ImapMailboxCreateRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxCreateResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxRenameResult> RenameMailboxAsync(
            ImapMailboxRenameRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxRenameResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxDeleteResult> DeleteMailboxAsync(
            ImapMailboxDeleteRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxDeleteResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxSelectResult> SelectMailboxAsync(
            ImapMailboxSelectRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxSelectResult>(new IOException("Worker unavailable"));

        public Task<ImapQuotaResult> GetQuotaAsync(
            ImapQuotaRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapQuotaResult>(new IOException("Worker unavailable"));

        public Task<ImapIdleSnapshotResult> GetIdleSnapshotAsync(
            ImapIdleSnapshotRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapIdleSnapshotResult>(new IOException("Worker unavailable"));

        public Task<ImapExpungeResult> ExpungeDeletedAsync(
            ImapExpungeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapExpungeResult>(new IOException("Worker unavailable"));

        public Task<ImapStoreResult> StoreFlagsAsync(
            ImapStoreRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapStoreResult>(new IOException("Worker unavailable"));

        public Task<ImapMoveResult> MoveMessagesAsync(
            ImapMoveRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMoveResult>(new IOException("Worker unavailable"));

        public Task<ImapCopyResult> CopyMessagesAsync(
            ImapCopyRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapCopyResult>(new IOException("Worker unavailable"));

        public Task<ImapAppendPreflightResult> CheckAppendCapacityAsync(
            ImapAppendPreflightRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapAppendPreflightResult>(new IOException("Worker unavailable"));

        public Task<ImapAppendResult> AppendMessagesAsync(
            ImapAppendRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapAppendResult>(new IOException("Worker unavailable"));

        public Task<ImapSearchResult> SearchMessagesAsync(
            ImapSearchRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapSearchResult>(new IOException("Worker unavailable"));

        public Task<ImapSortResult> SortMessagesAsync(
            ImapSortRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapSortResult>(new IOException("Worker unavailable"));

        public Task<ImapThreadResult> ThreadMessagesAsync(
            ImapThreadRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapThreadResult>(new IOException("Worker unavailable"));

        public Task<ImapMarkSeenResult> MarkMessagesSeenAsync(
            ImapMarkSeenRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMarkSeenResult>(new IOException("Worker unavailable"));

        public Task<ImapFetchPageResult> GetFetchPageAsync(
            ImapFetchPageRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapFetchPageResult>(new IOException("Worker unavailable"));
    }

    private sealed class UnavailableImapMailboxApplicationService(
        bool listingUnavailable = true,
        bool selectionMalformed = false,
        bool selectionAvailable = false,
        bool storeMalformed = false,
        bool moveMalformed = false,
        bool copyMalformed = false,
        bool appendCommitUnavailable = false,
        bool appendMalformed = false,
        bool searchMalformed = false,
        bool sortMalformed = false,
        bool threadMalformed = false,
        bool seenMalformed = false,
        bool fetchMalformed = false,
        bool fetchPaged = false,
        bool fetchDrift = false) : IImapApplicationService
    {
        public Guid? SelectedFolderId { get; set; }
        public List<int> FetchCursors { get; } = [];

        public Task<ImapIdentityResult> AuthenticatePasswordAsync(
            ImapPasswordAuthentication request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapIdentityResult(Guid.CreateVersion7(), request.Username));

        public Task<ImapIdentityResult> AuthenticateOAuthAsync(
            ImapOAuthAuthentication request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapIdentityResult(null, null));

        public Task<ImapMailboxListResult> ListMailboxesAsync(
            ImapMailboxListRequest request,
            CancellationToken cancellationToken = default) => listingUnavailable
            ? Task.FromException<ImapMailboxListResult>(new IOException("Worker unavailable"))
            : Task.FromResult(new ImapMailboxListResult(
                [new ImapMailboxInfo("user", "tenant.example.test", "INBOX", true, true)]));

        public Task<ImapMailboxStatusResult> GetMailboxStatusesAsync(
            ImapMailboxStatusRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxStatusResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxSubscriptionResult> SetMailboxSubscriptionAsync(
            ImapMailboxSubscriptionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxSubscriptionResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxCreateResult> CreateMailboxAsync(
            ImapMailboxCreateRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxCreateResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxRenameResult> RenameMailboxAsync(
            ImapMailboxRenameRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxRenameResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxDeleteResult> DeleteMailboxAsync(
            ImapMailboxDeleteRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMailboxDeleteResult>(new IOException("Worker unavailable"));

        public Task<ImapMailboxSelectResult> SelectMailboxAsync(
            ImapMailboxSelectRequest request,
            CancellationToken cancellationToken = default) => selectionMalformed
            ? Task.FromResult(new ImapMailboxSelectResult(new ImapSelectedMailbox(
                Guid.Empty, 1, 1, 1, "mailbox-id", 1, null, [], [], [])))
            : selectionAvailable
                ? Task.FromResult(new ImapMailboxSelectResult(new ImapSelectedMailbox(
                    SelectedFolderId ?? Guid.CreateVersion7(), 1, 1, 1,
                    "mailbox-id", 0, null, [], [], [])))
            : Task.FromException<ImapMailboxSelectResult>(new IOException("Worker unavailable"));

        public Task<ImapQuotaResult> GetQuotaAsync(
            ImapQuotaRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapQuotaResult>(new IOException("Worker unavailable"));

        public Task<ImapIdleSnapshotResult> GetIdleSnapshotAsync(
            ImapIdleSnapshotRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapIdleSnapshotResult>(new IOException("Worker unavailable"));

        public Task<ImapExpungeResult> ExpungeDeletedAsync(
            ImapExpungeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapExpungeResult>(new IOException("Worker unavailable"));

        public Task<ImapStoreResult> StoreFlagsAsync(
            ImapStoreRequest request,
            CancellationToken cancellationToken = default) => storeMalformed
            ? Task.FromResult(new ImapStoreResult(ImapStoreDisposition.Stored, [],
                [new ImapChangedMessage(0, 0, 0, false, false, false, false, false, [])]))
            : Task.FromException<ImapStoreResult>(new IOException("Worker unavailable"));

        public Task<ImapMoveResult> MoveMessagesAsync(
            ImapMoveRequest request,
            CancellationToken cancellationToken = default) => moveMalformed
            ? Task.FromResult(new ImapMoveResult(ImapMoveDisposition.Moved, 1,
                [1], [2], []))
            : Task.FromException<ImapMoveResult>(new IOException("Worker unavailable"));

        public Task<ImapCopyResult> CopyMessagesAsync(
            ImapCopyRequest request,
            CancellationToken cancellationToken = default) => copyMalformed
            ? Task.FromResult(new ImapCopyResult(ImapCopyDisposition.Copied, 1,
                [1], []))
            : Task.FromException<ImapCopyResult>(new IOException("Worker unavailable"));

        public Task<ImapAppendPreflightResult> CheckAppendCapacityAsync(
            ImapAppendPreflightRequest request,
            CancellationToken cancellationToken = default) =>
            appendCommitUnavailable || appendMalformed
                ? Task.FromResult(new ImapAppendPreflightResult(
                    ImapAppendPreflightDisposition.Ready))
                : Task.FromException<ImapAppendPreflightResult>(
                    new IOException("Worker unavailable"));

        public Task<ImapAppendResult> AppendMessagesAsync(
            ImapAppendRequest request,
            CancellationToken cancellationToken = default) => appendMalformed
            ? Task.FromResult(new ImapAppendResult(ImapAppendDisposition.Appended, 1, []))
            : Task.FromException<ImapAppendResult>(new IOException("Worker unavailable"));

        public Task<ImapSearchResult> SearchMessagesAsync(
            ImapSearchRequest request,
            CancellationToken cancellationToken = default) => searchMalformed
            ? Task.FromResult(new ImapSearchResult(true, null,
                [new ImapSearchMatch(0, 1)], null))
            : Task.FromException<ImapSearchResult>(new IOException("Worker unavailable"));

        public Task<ImapSortResult> SortMessagesAsync(
            ImapSortRequest request,
            CancellationToken cancellationToken = default) => sortMalformed
            ? Task.FromResult(new ImapSortResult(true, null,
                [new ImapSearchMatch(7, 1), new ImapSearchMatch(7, 2)], null))
            : Task.FromException<ImapSortResult>(new IOException("Worker unavailable"));

        public Task<ImapThreadResult> ThreadMessagesAsync(
            ImapThreadRequest request,
            CancellationToken cancellationToken = default) => threadMalformed
            ? Task.FromResult(new ImapThreadResult(true, null,
                [new ImapThreadNode(7, -1), new ImapThreadNode(7, 0)]))
            : Task.FromException<ImapThreadResult>(new IOException("Worker unavailable"));

        public Task<ImapMarkSeenResult> MarkMessagesSeenAsync(
            ImapMarkSeenRequest request,
            CancellationToken cancellationToken = default) => seenMalformed
            ? Task.FromResult(new ImapMarkSeenResult(true,
                [new ImapSeenMessage(Guid.CreateVersion7(), true, 7)]))
            : Task.FromException<ImapMarkSeenResult>(new IOException("Worker unavailable"));

        public Task<ImapFetchPageResult> GetFetchPageAsync(
            ImapFetchPageRequest request,
            CancellationToken cancellationToken = default)
        {
            if (fetchMalformed)
                return Task.FromResult(new ImapFetchPageResult(
                    true, 1, 1, 1, 0, true, []));
            if (SelectedFolderId is null)
                return Task.FromException<ImapFetchPageResult>(
                    new IOException("Worker unavailable"));
            if (fetchPaged)
            {
                FetchCursors.Add(request.AfterUid);
                var uid = request.AfterUid + 1;
                return Task.FromResult(new ImapFetchPageResult(
                    true, 2, 2, fetchDrift && uid == 2 ? 1 : 2, uid,
                    uid == 1,
                    [new ImapFetchMessage(Guid.CreateVersion7(), uid, uid, uid,
                        false, false, false, false, false, [], DateTime.UtcNow,
                        0, "sender@example.test", TestUsername, null, "test",
                        string.Empty, null, null, null, null, null, null)]));
            }

            var raw = "Subject: test\r\n\r\nbody\r\n"u8.ToArray();
            return Task.FromResult(new ImapFetchPageResult(true, 1, 1, 1, 1, false,
                [new ImapFetchMessage(Guid.CreateVersion7(), 1, 1, 1,
                    false, false, false, false, false, [],
                    DateTime.UtcNow, raw.Length, "sender@example.test",
                    TestUsername, null, "test", "body\r\n",
                    "Subject: test", null, null, null, null, raw)]));
        }
    }

    private sealed class ServerFixture(
        ServiceProvider services,
        IHostedService hostedService,
        StubEmailService emailService,
        StubMailSubmissionQueue mailQueue,
        NativeAuthenticationObserver authenticationObserver) : IAsyncDisposable
    {
        public StubEmailService EmailService { get; } = emailService;
        public StubMailSubmissionQueue MailQueue { get; } = mailQueue;
        public NativeAuthenticationObserver AuthenticationObserver { get; } = authenticationObserver;

        public static async Task<ServerFixture> StartSmtpAsync(
            EnvironmentConfig environment,
            int port,
            ILogger<SmtpServerService>? logger = null,
            IGatewayTrafficJournal? journal = null,
            Func<IMailAuthenticator, IMailAuthenticator>? authenticationDecorator = null)
        {
            var (services, emailService, mailQueue, observer) = CreateServices(
                environment, authenticationDecorator: authenticationDecorator);

            // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
            SmtpServerService? hostedService = new SmtpServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                logger ?? NullLogger<SmtpServerService>.Instance,
                journal);

#pragma warning restore CA2000

            try
            {

                // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
                var fixture = new ServerFixture(services, hostedService, emailService, mailQueue, observer);

#pragma warning restore CA2000
                await fixture.StartAsync(port).ConfigureAwait(false);
                hostedService = null;
                return fixture;
            }
            finally
            {

                // Successful transfer clears the resource; initialization exceptions leave it non-null for finally cleanup.
#pragma warning disable CA1508
                if (hostedService is not null)
                {
                    try { await hostedService.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                    finally { hostedService.Dispose(); await services.DisposeAsync().ConfigureAwait(false); }
                }

#pragma warning restore CA1508
            }

        }

        public static async Task<ServerFixture> StartImapAsync(
            EnvironmentConfig environment,
            int port,
            ILogger<ImapServerService>? logger = null,
            IImapApplicationService? applicationService = null,
            IGatewayTrafficJournal? journal = null,
            Func<IMailAuthenticator, IMailAuthenticator>? authenticationDecorator = null)
        {
            var (services, emailService, mailQueue, observer) = CreateServices(
                environment, imapApplicationService: applicationService,
                authenticationDecorator: authenticationDecorator);

            // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
            ImapServerService? hostedService = new ImapServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                logger ?? NullLogger<ImapServerService>.Instance,
                journal);

#pragma warning restore CA2000

            try
            {

                // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
                var fixture = new ServerFixture(services, hostedService, emailService, mailQueue, observer);

#pragma warning restore CA2000
                await fixture.StartAsync(port).ConfigureAwait(false);
                hostedService = null;
                return fixture;
            }
            finally
            {

                // Successful transfer clears the resource; initialization exceptions leave it non-null for finally cleanup.
#pragma warning disable CA1508
                if (hostedService is not null)
                {
                    try { await hostedService.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                    finally { hostedService.Dispose(); await services.DisposeAsync().ConfigureAwait(false); }
                }

#pragma warning restore CA1508
            }

        }

        public static async Task<ServerFixture> StartPop3Async(
            EnvironmentConfig environment,
            int port,
            ILogger<Pop3ServerService>? logger = null,
            IGatewayTrafficJournal? journal = null,
            IPop3ApplicationService? applicationService = null,
            Func<IMailAuthenticator, IMailAuthenticator>? authenticationDecorator = null)
        {
            var (services, emailService, mailQueue, observer) = CreateServices(
                environment, applicationService, authenticationDecorator: authenticationDecorator);

            // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
            Pop3ServerService? hostedService = new Pop3ServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                logger ?? NullLogger<Pop3ServerService>.Instance,
                services.GetRequiredService<IPop3MaildropLeaseStore>(),
                journal);

#pragma warning restore CA2000

            try
            {

                // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
                var fixture = new ServerFixture(services, hostedService, emailService, mailQueue, observer);

#pragma warning restore CA2000
                await fixture.StartAsync(port).ConfigureAwait(false);
                hostedService = null;
                return fixture;
            }
            finally
            {

                // Successful transfer clears the resource; initialization exceptions leave it non-null for finally cleanup.
#pragma warning disable CA1508
                if (hostedService is not null)
                {
                    try { await hostedService.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                    finally { hostedService.Dispose(); await services.DisposeAsync().ConfigureAwait(false); }
                }

#pragma warning restore CA1508
            }

        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Declared output failures are contained; even an unexpected diagnostic fault cannot skip shutdown.
                _ = AuthenticationObserver.WritePoint(AuthenticationPoint.FixtureStopping);
            }
            finally
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await hostedService.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    (hostedService as IDisposable)?.Dispose();
                    await services.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        public async Task DisableOwnedAddressAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var address = await database.Addresses.SingleAsync(item => item.Domain == "tenant.example.test").ConfigureAwait(false);
            address.IsActive = false;
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        public async Task SetUserQuotaAsync(long quotaBytes)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var user = await database.Users.SingleAsync(item => item.Username == TestUsername).ConfigureAwait(false);
            user.QuotaBytes = quotaBytes;
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        public async Task<int> CountStoredEmailsAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            return await database.Emails.CountAsync().ConfigureAwait(false);
        }

        public async Task<int> CountExpungedUidsAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            return await database.ExpungedUids.CountAsync().ConfigureAwait(false);
        }

        public async Task<string> CreateOAuthAccessTokenAsync(string scopeName)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var userId = await database.Users
                .Where(user => user.Username == TestUsername)
                .Select(user => user.Id)
                .SingleAsync().ConfigureAwait(false);
            var pair = await scope.ServiceProvider.GetRequiredService<IOAuthTokenService>()
                .CreateGrantAsync(
                    userId,
                    "thunderbird",
                    "Thunderbird protocol test",
                    ["offline_access", scopeName]).ConfigureAwait(false);
            return pair?.AccessToken
                ?? throw new InvalidOperationException("The OAuth access token was not created.");
        }

        private static (
            ServiceProvider Services,
            StubEmailService EmailService,
            StubMailSubmissionQueue MailQueue,
            NativeAuthenticationObserver AuthenticationObserver) CreateServices(
                EnvironmentConfig environment,
                IPop3ApplicationService? applicationService = null,
                IImapApplicationService? imapApplicationService = null,
                Func<IMailAuthenticator, IMailAuthenticator>? authenticationDecorator = null)
        {
            var emailService = new StubEmailService();
            var mailQueue = new StubMailSubmissionQueue();
            var observer = new NativeAuthenticationObserver();
            var databaseName = $"transport-{Guid.NewGuid():N}";
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton(environment);
            serviceCollection.AddSingleton<IEmailService>(emailService);
            serviceCollection.AddSingleton<IMailSubmissionQueue>(mailQueue);
            serviceCollection.AddScoped<ISmtpApplicationService, SmtpApplicationService>();
            if (imapApplicationService is null)
                serviceCollection.AddScoped<IImapApplicationService, ImapApplicationService>();
            else
                serviceCollection.AddSingleton(imapApplicationService);
            if (applicationService is null)
                serviceCollection.AddScoped<IPop3ApplicationService, Pop3ApplicationService>();
            else
                serviceCollection.AddSingleton(applicationService);
            serviceCollection.AddSingleton<IPop3MaildropLeaseStore, InMemoryPop3MaildropLeaseStore>();
            serviceCollection.AddScoped<ISenderAuthorizationService, SenderAuthorizationService>();
            serviceCollection.AddScoped<MailAuthenticator>();
            serviceCollection.AddScoped<IMailAuthenticator>(provider =>
            {
                IMailAuthenticator authenticator = provider.GetRequiredService<MailAuthenticator>();
                if (authenticationDecorator is not null)
                    authenticator = authenticationDecorator(authenticator);
                return new ObservedMailAuthenticator(authenticator, observer);
            });
            serviceCollection.AddScoped<IOAuthTokenService, OAuthTokenService>();
            serviceCollection.AddSingleton<ILargeObjectStore, InMemoryLargeObjectStore>();
            serviceCollection.AddScoped(provider => new LargeObjectTransactionEffects(
                provider.GetRequiredService<ILargeObjectStore>(),
                NullLogger<LargeObjectTransactionEffects>.Instance));
            serviceCollection.AddScoped<MailboxMessageContentService>();
            serviceCollection.AddDbContext<EmailDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
            serviceCollection.AddLogging();
            var services = serviceCollection.BuildServiceProvider();

            try
            {
                SeedAccount(services);
                return (services, emailService, mailQueue, observer);
            }
            catch
            {
                services.Dispose();
                throw;
            }
        }

        private static readonly string[] ProtocolFolders = [DefaultFolders.Inbox, DefaultFolders.Sent, DefaultFolders.Trash];

        private static void SeedAccount(ServiceProvider services)
        {
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
                    Domain = "tenant.example.test",
                    IsActive = true,
                    Company = company,
                };
                var user = new UserDB
                {
                    Id = Guid.CreateVersion7(),
                    Username = TestUsername,
                    PasswordHash = TestPasswordHash,
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

                foreach (var name in ProtocolFolders)
                {
                    database.Folders.Add(new FolderDB { Id = Guid.CreateVersion7(), Name = name, Inbox = inbox });
                }
                database.SaveChanges();
            }

        }

        private async Task StartAsync(int port)
        {
            await hostedService.StartAsync(CancellationToken.None).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(20, timeout.Token).ConfigureAwait(false);
                }
            }

            throw new TimeoutException($"The test server did not listen on port {port}.");
        }

        public async Task<EmailDB> GetStoredEmailAsync(string folderName)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var email = await database.Emails
                .AsNoTracking()
                .Include(email => email.Folder)
                .SingleAsync(email => email.Folder.Name == folderName).ConfigureAwait(false);
            email.RawMessage = await scope.ServiceProvider
                .GetRequiredService<MailboxMessageContentService>()
                .ReadAsync(email, CancellationToken.None).ConfigureAwait(false);
            return email;
        }

        public async Task<EmailDB> GetPersistedEmailAsync(string folderName)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            return await database.Emails
                .AsNoTracking()
                .Include(email => email.Folder)
                .SingleAsync(email => email.Folder.Name == folderName).ConfigureAwait(false);
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
                .ToListAsync().ConfigureAwait(false);
        }

        public async Task<EmailDB> GetStoredEmailByUidAsync(int uid)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            return await database.Emails
                .AsNoTracking()
                .SingleAsync(email => email.Uid == uid).ConfigureAwait(false);
        }

        public async Task<long> SeedSentMessagesWithReverseDatesAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
            var effects = scope.ServiceProvider.GetRequiredService<LargeObjectTransactionEffects>();
            var marker = effects.Mark();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Sent).ConfigureAwait(false);
            var first = CreateStoredEmail(folder.Id, uid: 1, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
            var second = CreateStoredEmail(folder.Id, uid: 2, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            try
            {
                await content.SetAsync(first, MailboxMessageContentService.BuildLegacyRawMessage(first), CancellationToken.None).ConfigureAwait(false);
                await content.SetAsync(second, MailboxMessageContentService.BuildLegacyRawMessage(second), CancellationToken.None).ConfigureAwait(false);
                await database.Emails.AddRangeAsync(first, second).ConfigureAwait(false);
                folder.NextUid = 3;
                folder.HighestModSeq = 2;
                await database.SaveChangesAsync().ConfigureAwait(false);
                await effects.CommitAsync(marker).ConfigureAwait(false);
                Assert.AreEqual(first.SizeBytes, second.SizeBytes);
                return first.SizeBytes;
            }
            catch
            {
                await effects.RollbackAsync(marker).ConfigureAwait(false);
                throw;
            }
        }

        public async Task SeedPop3MessagesAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
            var effects = scope.ServiceProvider.GetRequiredService<LargeObjectTransactionEffects>();
            var marker = effects.Mark();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Inbox).ConfigureAwait(false);
            var firstRaw =
                $"From: sender@example.net\r\nTo: {TestUsername}\r\nSubject: POP first\r\n\r\n" +
                ".leading dot\r\nsecond body line\r\n";
            var secondRaw =
                $"From: sender@example.net\nTo: {TestUsername}\nSubject: POP second\n\n" +
                "second message without canonical endings";
            var first = new EmailDB
            {
                Id = Guid.CreateVersion7(),
                Sender = "sender@example.net",
                Recipient = TestUsername,
                Subject = "POP first",
                Body = ".leading dot\r\nsecond body line\r\n",
                RawHeaders = $"From: sender@example.net\r\nTo: {TestUsername}\r\nSubject: POP first",
                Uid = 1,
                ModSeq = 1,
                FolderId = folder.Id,
                ReceivedAt = DateTime.UtcNow.AddMinutes(-1),
            };
            var second = new EmailDB
            {
                Id = Guid.CreateVersion7(),
                Sender = "sender@example.net",
                Recipient = TestUsername,
                Subject = "POP second",
                Body = "second message without canonical endings",
                RawHeaders = $"From: sender@example.net\nTo: {TestUsername}\nSubject: POP second",
                Uid = 2,
                ModSeq = 2,
                FolderId = folder.Id,
                ReceivedAt = DateTime.UtcNow,
            };
            await content.SetAsync(first, MailWireEncoding.Instance.GetBytes(firstRaw), CancellationToken.None).ConfigureAwait(false);
            await content.SetAsync(second, MailWireEncoding.Instance.GetBytes(secondRaw), CancellationToken.None).ConfigureAwait(false);
            await database.Emails.AddRangeAsync(first, second).ConfigureAwait(false);
            folder.NextUid = 3;
            folder.HighestModSeq = 2;
            await database.SaveChangesAsync().ConfigureAwait(false);
            await effects.CommitAsync(marker).ConfigureAwait(false);
        }

        public async Task SeedInboxMessagesForSearchAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Inbox).ConfigureAwait(false);
            await database.Emails.AddRangeAsync(
SearchVector1(folder.Id),
SearchVector2(folder.Id),
SearchVector3(folder.Id)).ConfigureAwait(false);
            folder.NextUid = 4;
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        public async Task SeedInboxMessagesForSortAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Inbox).ConfigureAwait(false);
            await database.Emails.AddRangeAsync(
SortVector1(folder.Id),
SortVector2(folder.Id),
SortVector3(folder.Id),
SortVector4(folder.Id),
SortVector5(folder.Id)).ConfigureAwait(false);
            folder.NextUid = 51;
            folder.HighestModSeq = 5;
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        public async Task SeedInboxMessagesForReferencesAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Inbox).ConfigureAwait(false);
            await database.Emails.AddRangeAsync(
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
                CreateThreadEmail(folder.Id, 212, 31, "SHARED", "<dummy-five@example.net>")).ConfigureAwait(false);
            folder.NextUid = 213;
            folder.HighestModSeq = 31;
            await database.SaveChangesAsync().ConfigureAwait(false);
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
                .Append(System.Globalization.CultureInfo.InvariantCulture, $"Date: {sentAt:R}\r\n")
                .Append(System.Globalization.CultureInfo.InvariantCulture, $"Subject: {headerSubject}\r\n")
                .Append(System.Globalization.CultureInfo.InvariantCulture, $"Message-ID: {messageId}\r\n");
            if (references is not null)
                headers.Append(System.Globalization.CultureInfo.InvariantCulture, $"References: {references}\r\n");
            if (inReplyTo is not null)
                headers.Append(System.Globalization.CultureInfo.InvariantCulture, $"In-Reply-To: {inReplyTo}\r\n");

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
        private static EmailDB SearchVector1(Guid folderId) => new EmailDB
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
            FolderId = folderId,
            ReceivedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        }; private static EmailDB SearchVector2(Guid folderId) => new EmailDB
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
            FolderId = folderId,
            ReceivedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        }; private static EmailDB SearchVector3(Guid folderId) => new EmailDB
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
            FolderId = folderId,
            ReceivedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
        }; private static EmailDB SortVector1(Guid folderId) => CreateSortEmail(
            folderId,
            uid: 10,
            new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            size: 400,
            subject: "Re: [list] Topic (fwd)",
            "From: Zulu Person <zeta@example.net>\r\n" +
            "To: Bravo Person <bravo@example.net>\r\n" +
            "Cc: Delta Person <delta@example.net>\r\n" +
            "Date: Mon, 2 Feb 2026 10:00:00 +0000\r\n" +
            "Subject: Re: [list] Topic (fwd)"); private static EmailDB SortVector2(Guid folderId) => CreateSortEmail(
            folderId,
            uid: 20,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            size: 100,
            subject: "topic",
            "From: Alpha Person <alpha@example.net>\r\n" +
            "To: Delta Person <delta@example.net>\r\n" +
            "Cc: Charlie Person <charlie@example.net>\r\n" +
            "Date: Tue, 3 Feb 2026 10:00:00 +0000\r\n" +
            "Subject: topic"); private static EmailDB SortVector3(Guid folderId) => CreateSortEmail(
            folderId,
            uid: 30,
            new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            size: 300,
            subject: "Re: Äpfel",
            "From: Charlie Person <charlie@example.net>\r\n" +
            "To: Alpha Person <alpha@example.net>\r\n" +
            "Subject: =?UTF-8?Q?Re=3A_=C3=84pfel?="); private static EmailDB SortVector4(Guid folderId) => CreateSortEmail(
            folderId,
            uid: 40,
            new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            size: 200,
            subject: "Apfel",
            "From: Bravo Person <bravo@example.net>\r\n" +
            "To: Charlie Person <charlie@example.net>\r\n" +
            "Cc: Alpha Person <alpha@example.net>\r\n" +
            "Date: Sun, 1 Feb 2026 10:00:00 +0000\r\n" +
            "Subject: Apfel"); private static EmailDB SortVector5(Guid folderId) => CreateSortEmail(
            folderId,
            uid: 50,
            new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            size: 400,
            subject: "Fwd: Topic",
            "From: Zulu Person <zeta@example.net>\r\n" +
            "To: Bravo Person <bravo@example.net>\r\n" +
            "Cc: Delta Person <delta@example.net>\r\n" +
            "Date: Mon, 2 Feb 2026 10:00:00 +0000\r\n" +
            "Subject: Fwd: Topic");
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
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return new ProtocolConnection(client);
        }

        public Task<string> ReadLineAsync() => ReadLineAsync(TimeSpan.FromSeconds(3));

        public async Task<string> ReadLineAsync(TimeSpan timeoutDuration)
        {
            using var timeout = new CancellationTokenSource(timeoutDuration);
            return await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The server closed the protocol stream.");
        }

        public async Task<string> ReadCharactersAsync(int count)
        {
            var buffer = new char[count];
            var totalRead = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (totalRead < count)
            {
                var read = await _reader.ReadAsync(
                    buffer.AsMemory(totalRead, count - totalRead),
                    timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("The server closed the protocol stream.");
                totalRead += read;
            }
            return new string(buffer);
        }

        public async Task<string> ReadSmtpResponseAsync()
        {
            var response = new StringBuilder();
            while (true)
            {
                var line = await ReadLineAsync().ConfigureAwait(false);
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
            await _writer.WriteAsync(value).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }

        public Task WriteUtf8LineAsync(string line) => WriteUtf8RawAsync(line + "\r\n");

        public async Task WriteUtf8RawAsync(string value)
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            await _stream.WriteAsync(Encoding.UTF8.GetBytes(value)).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }

        public async Task WriteBytesAsync(ReadOnlyMemory<byte> value)
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            await _stream.WriteAsync(value).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }

        public async Task<string> ReadUtf8LineAsync()
        {
            var wireValue = await ReadLineAsync().ConfigureAwait(false);
            return new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetString(Encoding.Latin1.GetBytes(wireValue));
        }

        public async Task UpgradeToTlsAsync(string hostName)
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            _reader.Dispose();
            await _writer.DisposeAsync().ConfigureAwait(false);

            var tlsStream = new SslStream(
                _stream,
                leaveInnerStreamOpen: false,
                (_, certificate, _, errors) => TestCertificateFactory.IsTrusted(certificate, errors));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tlsStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = hostName },
                timeout.Token).ConfigureAwait(false);

            _stream = tlsStream;
            _reader = CreateReader(_stream);
            _writer = CreateWriter(_stream);
        }

        public async Task UpgradeToDeflateAsync()
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            _reader.Dispose();
            await _writer.DisposeAsync().ConfigureAwait(false);

            var transport = _stream;

            DeflateStream? inflater = null;
            DeflateStream? deflater = null;
            try
            {

                // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
                inflater = new DeflateStream(transport, CompressionMode.Decompress, leaveOpen: true);

#pragma warning restore CA2000

                // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
                deflater = new DeflateStream(transport, CompressionLevel.Fastest, leaveOpen: true);

#pragma warning restore CA2000
                _stream = new TestDuplexStream(inflater, deflater, transport);
                inflater = null;
                deflater = null;
                _reader = CreateReader(_stream);
                _writer = CreateWriter(_stream);
            }
            finally
            {
                if (inflater is not null) await inflater.DisposeAsync().ConfigureAwait(false);
                if (deflater is not null) await deflater.DisposeAsync().ConfigureAwait(false);
            }

        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _writer.DisposeAsync().ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
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
            await readStream.DisposeAsync().ConfigureAwait(false);
            await writeStream.DisposeAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
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
        public bool ThrowOnCanReceive { get; set; }

        public Task<bool> CanReceiveAsync(
            string recipient,
            CancellationToken cancellationToken = default) => ThrowOnCanReceive
                ? throw new IOException("The application worker is unavailable.")
                : Task.FromResult(true);

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

    private sealed class RecordingSmtpJournal : IGatewayTrafficJournal
    {
        public ConcurrentQueue<GatewayTrafficRecord> Records { get; } = new();
        public bool RejectWrites { get; init; }

        public Task AppendAsync(
            GatewayTrafficRecord record,
            CancellationToken cancellationToken = default)
        {
            if (RejectWrites)
                throw new InvalidOperationException("The journal is unavailable.");
            Records.Enqueue(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GatewayTrafficRecord>>(
                Records.Where(record => record.SessionId == sessionId).ToArray());
    }
    private static readonly string[] ExpectedVector1 = new[] { "$label1", "$label2" };
    private static readonly string[] ExpectedVector2 = new[] { "Final" };
    private static readonly int[] ExpectedVector3 = new[] { 0, 1 };
    private static readonly string[] ExpectedVector4 = new[] { "UID 1", "UID 2" };
}
