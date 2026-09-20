using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MimeKit;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class VacationResponderTests
{
    private const string AccountAddress = "admin@mk8n.com";
    private const string AliasAddress = "support@mk8n.com";
    private const string SenderAddress = "sender@example.net";

    [TestMethod]
    public async Task ResponseIsSuppressedWhenTheRecipientIsNotNamed()
    {
        await using var fixture = await VacationFixture.CreateAsync(includeAlias: false);
        var rawMessage =
            "From: sender@example.net\r\n" +
            "To: another@example.org\r\n" +
            "Subject: recipient check\r\n\r\n" +
            "body\r\n";

        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress,
            AccountAddress,
            rawMessage,
            DefaultFolders.Inbox,
            Guid.CreateVersion7()));

        Assert.IsNull(fixture.Queue.Submission);
        Assert.AreEqual(0, await fixture.Database.JmapVacationReplies.CountAsync());
    }

    [TestMethod]
    public async Task ResponseRecognizesDeliveredAliasesInResentRecipientHeaders()
    {
        await using var fixture = await VacationFixture.CreateAsync(includeAlias: true);
        var rawMessage =
            "From: sender@example.net\r\n" +
            "To: another@example.org\r\n" +
            "Resent-Cc: Support <support@mk8n.com>\r\n" +
            "References: <root@example.net> <ancestor@example.net>\r\n" +
            "Message-ID: <original@example.net>\r\n" +
            "Subject: alias check\r\n\r\n" +
            "body\r\n";

        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress,
            AliasAddress,
            rawMessage,
            DefaultFolders.Inbox,
            Guid.CreateVersion7()));

        var submission = fixture.Queue.Submission;
        Assert.IsNotNull(submission);
        Assert.AreEqual(string.Empty, submission.EnvelopeSender);
        Assert.AreEqual(SenderAddress, submission.Recipients.Single().Address);
        Assert.IsFalse(submission.Recipients.Single().IsLocal);

        using var response = MimeMessage.Load(
            new MemoryStream(Encoding.Latin1.GetBytes(submission.RawMessage)));
        Assert.AreEqual(AccountAddress, response.From.Mailboxes.Single().Address);
        Assert.AreEqual("auto-replied", response.Headers[HeaderId.AutoSubmitted]);
        Assert.AreEqual("Auto: alias check", response.Subject);
        Assert.AreEqual("original@example.net", response.InReplyTo);
        CollectionAssert.AreEqual(
            new[] { "root@example.net", "ancestor@example.net", "original@example.net" },
            response.References.ToArray());
        Assert.AreEqual(1, fixture.Database.JmapVacationReplies.Local.Count);
    }

    [TestMethod]
    public async Task ResponseBuildsReferencesFromInReplyToWhenNoReferencesArePresent()
    {
        await using var fixture = await VacationFixture.CreateAsync(includeAlias: false);
        var rawMessage =
            "From: sender@example.net\r\n" +
            "To: admin@mk8n.com\r\n" +
            "In-Reply-To: <root@example.net>\r\n" +
            "Message-ID: <original@example.net>\r\n" +
            "Subject: thread check\r\n\r\n" +
            "body\r\n";

        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress,
            AccountAddress,
            rawMessage,
            DefaultFolders.Inbox,
            Guid.CreateVersion7()));

        using var response = MimeMessage.Load(new MemoryStream(
            Encoding.Latin1.GetBytes(fixture.Queue.Submission!.RawMessage)));
        CollectionAssert.AreEqual(
            new[] { "root@example.net", "original@example.net" },
            response.References.ToArray());
    }

    [TestMethod]
    public async Task ParameterizedManualAutoSubmittedHeaderStillReceivesAResponse()
    {
        await using var fixture = await VacationFixture.CreateAsync(includeAlias: false);
        var rawMessage =
            "From: sender@example.net\r\n" +
            "To: admin@mk8n.com\r\n" +
            "Auto-Submitted: (origin) no (manual); x-client=\"mail app\"\r\n" +
            "Subject: parameter check\r\n\r\n" +
            "body\r\n";

        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress,
            AccountAddress,
            rawMessage,
            DefaultFolders.Inbox,
            Guid.CreateVersion7()));

        Assert.IsNotNull(fixture.Queue.Submission);
    }

    [TestMethod]
    public async Task AnyAutomaticAutoSubmittedHeaderSuppressesAResponse()
    {
        await using var fixture = await VacationFixture.CreateAsync(includeAlias: false);
        var rawMessage =
            "From: sender@example.net\r\n" +
            "To: admin@mk8n.com\r\n" +
            "Auto-Submitted: no\r\n" +
            "Auto-Submitted: auto-generated; x-source=scheduler\r\n" +
            "Subject: loop check\r\n\r\n" +
            "body\r\n";

        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress,
            AccountAddress,
            rawMessage,
            DefaultFolders.Inbox,
            Guid.CreateVersion7()));

        Assert.IsNull(fixture.Queue.Submission);
        Assert.AreEqual(0, await fixture.Database.JmapVacationReplies.CountAsync());
    }

    [TestMethod]
    public async Task RepeatSuppressionUsesCultureInvariantSenderKeys()
    {
        await using var fixture = await VacationFixture.CreateAsync(includeAlias: true);
        const string sender = "INFO@example.net";
        var rawMessage =
            "From: INFO@example.net\r\n" +
            "To: support@mk8n.com\r\n" +
            "Subject: culture check\r\n\r\n" +
            "body\r\n";
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
                sender,
                AliasAddress,
                rawMessage,
                DefaultFolders.Inbox,
                Guid.CreateVersion7()));
            Assert.IsNotNull(fixture.Queue.Submission);
            await fixture.Database.SaveChangesAsync();
            Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
                sender,
                AliasAddress,
                rawMessage,
                DefaultFolders.Inbox,
                Guid.CreateVersion7()));
            await fixture.Database.SaveChangesAsync();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.AreEqual(1, await fixture.Database.JmapVacationReplies.CountAsync());
    }

    private sealed class VacationFixture(
        EmailDbContext database,
        CapturingQueue queue,
        VacationResponder responder) : IAsyncDisposable
    {
        public EmailDbContext Database { get; } = database;
        public CapturingQueue Queue { get; } = queue;
        public VacationResponder Responder { get; } = responder;

        public static async Task<VacationFixture> CreateAsync(bool includeAlias)
        {
            var options = new DbContextOptionsBuilder<EmailDbContext>()
                .UseInMemoryDatabase($"vacation-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var database = new EmailDbContext(options);
            await database.Database.EnsureCreatedAsync();

            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "Vacation Test",
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
                Username = AccountAddress,
                PasswordHash = "unused",
                Company = company,
            };
            var account = new InboxDB
            {
                Id = Guid.CreateVersion7(),
                Name = "admin",
                Address = address,
                Owner = user,
            };
            database.Inboxes.Add(account);
            if (includeAlias)
            {
                database.Inboxes.Add(new InboxDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "support",
                    Address = address,
                    Owner = user,
                    AliasForInbox = account,
                });
            }
            database.JmapVacationResponses.Add(new JmapVacationResponseDB
            {
                AccountId = account.Id,
                IsEnabled = true,
                TextBody = "I am away.",
            });
            await database.SaveChangesAsync();

            var queue = new CapturingQueue();
            var environment = new EnvironmentConfig
            {
                Smtp = new SmtpConfig { Hostname = "email.mk8n.com" },
                Limits = new LimitsConfig
                {
                    MaxMessageSizeBytes = 1024 * 1024,
                    MaxRecipientsPerMessage = 10,
                },
            };
            var responder = new VacationResponder(
                database,
                new EmailService(database),
                queue,
                environment,
                new FixedTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero)));
            return new VacationFixture(database, queue, responder);
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class CapturingQueue : IMailSubmissionQueue
    {
        public MailSubmission? Submission { get; private set; }

        public Task<Guid> EnqueueAsync(
            MailSubmission submission,
            CancellationToken cancellationToken = default)
        {
            Submission = submission;
            return Task.FromResult(submission.QueueId);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
