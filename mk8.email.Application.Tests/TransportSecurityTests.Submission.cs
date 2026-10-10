using System.Text;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Tests;

internal sealed partial class TransportSecurityTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(10_000)]
    public async Task SubmissionRequiresAuthenticationAfterTlsAndAllowsAuthenticatedMail(bool implicitTls)
    {
        var port = ReservePort();
        var environment = implicitTls
            ? CreateEnvironment(smtpImplicitTlsPort: port)
            : CreateEnvironment(submissionPort: port);
        var journal = new RecordingSmtpJournal();
        var server = await ServerFixture.StartSmtpAsync(environment, port, journal: journal).ConfigureAwait(false);
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await PrepareSecureSubmissionConnectionAsync(connection, implicitTls).ConfigureAwait(false);
        await RefuseUnauthenticatedEnvelopeAsync(connection).ConfigureAwait(false);
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
        await connection.WriteLineAsync($"AUTH PLAIN {credentials}").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "235 ", StringComparison.Ordinal);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "250 ", StringComparison.Ordinal);
        await QueueToLocalRecipientAsync(connection, TestUsername).ConfigureAwait(false);
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
        Assert.IsNotNull(server.MailQueue.LastSubmission!.AuthenticatedUser);
        await connection.WriteLineAsync("QUIT").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "221 ", StringComparison.Ordinal);
        Assert.IsTrue(journal.Records.Any(record => string.Equals(record.Direction, GatewayTrafficDirections.Inbound, StringComparison.Ordinal)));
        Assert.IsTrue(journal.Records.Any(record => string.Equals(record.Direction, GatewayTrafficDirections.Outbound, StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(10_000)]
    public async Task SubmissionAuthenticationCanBeExplicitlyDisabledForLocalDelivery(bool implicitTls)
    {
        var port = ReservePort();
        var environment = implicitTls
            ? CreateEnvironment(smtpImplicitTlsPort: port, requireAuthentication: false)
            : CreateEnvironment(submissionPort: port, requireAuthentication: false);
        var server = await ServerFixture.StartSmtpAsync(environment, port).ConfigureAwait(false);
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await PrepareSecureSubmissionConnectionAsync(connection, implicitTls).ConfigureAwait(false);
        await connection.WriteLineAsync("MAIL FROM:<sender@example.test>").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "250 ", StringComparison.Ordinal);
        await QueueToLocalRecipientAsync(connection, "sender@example.test").ConfigureAwait(false);
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
        Assert.IsNull(server.MailQueue.LastSubmission!.AuthenticatedUser);
        await connection.WriteLineAsync("QUIT").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "221 ", StringComparison.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task AnonymousInboundMailStillQueuesWhenSubmissionAuthenticationIsRequired()
    {
        var port = ReservePort();
        var server = await ServerFixture.StartSmtpAsync(CreateEnvironment(smtpPort: port), port).ConfigureAwait(false);
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO inbound.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("MAIL FROM:<sender@example.test>").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "250 ", StringComparison.Ordinal);
        await QueueToLocalRecipientAsync(connection, "sender@example.test").ConfigureAwait(false);
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
        Assert.IsNull(server.MailQueue.LastSubmission!.AuthenticatedUser);
        Assert.IsTrue(server.MailQueue.LastSubmission.Recipients.Single().IsLocal);
        await connection.WriteLineAsync("QUIT").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "221 ", StringComparison.Ordinal);
    }

    private static async Task PrepareSecureSubmissionConnectionAsync(ProtocolConnection connection, bool implicitTls)
    {
        if (implicitTls) await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "220 ", StringComparison.Ordinal);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
        if (implicitTls) return;
        await connection.WriteLineAsync("STARTTLS").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "220 ", StringComparison.Ordinal);
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.WriteLineAsync("EHLO client.example").ConfigureAwait(false);
        await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
    }

    private static async Task RefuseUnauthenticatedEnvelopeAsync(ProtocolConnection connection)
    {
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>").ConfigureAwait(false);
        Assert.AreEqual("530 5.7.0 Authentication required", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync("RCPT TO:<postmaster@tenant.example.test>").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "503 ", StringComparison.Ordinal);
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "503 ", StringComparison.Ordinal);
    }

    private static async Task QueueToLocalRecipientAsync(ProtocolConnection connection, string sender)
    {
        await connection.WriteLineAsync("RCPT TO:<postmaster@tenant.example.test>").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "250 ", StringComparison.Ordinal);
        await connection.WriteLineAsync("DATA").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "354 ", StringComparison.Ordinal);
        await connection.WriteRawAsync($"From: {sender}\r\nTo: postmaster@tenant.example.test\r\nSubject: mail canary\r\n\r\nbody\r\n.\r\n").ConfigureAwait(false);
        StringAssert.StartsWith(await connection.ReadLineAsync().ConfigureAwait(false), "250 2.0.0 Queued as ", StringComparison.Ordinal);
    }
}
