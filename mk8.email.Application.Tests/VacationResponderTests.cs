using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class VacationResponderTests
{
    private const string AccountAddress = "admin@mk8n.com";
    private const string AliasAddress = "support@mk8n.com";
    private const string SenderAddress = "sender@example.net";
    private const string DeliverableMessage =
        "From: sender@example.net\r\nTo: admin@mk8n.com\r\nSubject: Away\r\n\r\nbody\r\n";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(8, true, false)]
    [DataRow(6, false, false)]
    [DataRow(7, false, true)]
    [DataRow(8, false, true)]
    public async Task RepeatSuppressionRetainsDeliveryAndSevenDayBoundaries(int days, bool replay, bool queued)
    {
        await using var fixture = await VacationFixture.CreateAsync(includeAlias: false).ConfigureAwait(false);
        var accountId = (await fixture.Database.JmapVacationResponses.SingleAsync().ConfigureAwait(false)).AccountId;
        var previousId = Guid.CreateVersion7();
        var previousTime = Now.UtcDateTime.AddDays(-days);
        var reply = new JmapVacationReplyDB
        {
            Id = Guid.CreateVersion7(),
            AccountId = accountId,
            SenderAddress = SenderAddress,
            LastDeliveryId = previousId,
            LastSentAt = previousTime,
        };
        fixture.Database.JmapVacationReplies.Add(reply);
        await fixture.Database.SaveChangesAsync().ConfigureAwait(false);
        var deliveryId = replay ? previousId : Guid.CreateVersion7();
        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress, AccountAddress, DeliverableMessage, DefaultFolders.Inbox, deliveryId).ConfigureAwait(false));
        Assert.AreEqual(queued ? 1 : 0, fixture.Queue.EnqueueCalls);
        Assert.AreEqual(queued ? deliveryId : previousId, reply.LastDeliveryId);
        Assert.AreEqual(queued ? Now.UtcDateTime : previousTime, reply.LastSentAt);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedResponseDoesNotCreateOrChangeReplyHistory(bool existingReply)
    {
        await using var fixture = await VacationFixture.CreateAsync(
            includeAlias: false, maxMessageSizeBytes: 1).ConfigureAwait(false);
        var accountId = (await fixture.Database.JmapVacationResponses.SingleAsync().ConfigureAwait(false)).AccountId;
        var previousId = Guid.CreateVersion7();
        var previousTime = Now.UtcDateTime.AddDays(-8);
        if (existingReply)
        {
            fixture.Database.JmapVacationReplies.Add(new JmapVacationReplyDB
            {
                Id = Guid.CreateVersion7(),
                AccountId = accountId,
                SenderAddress = SenderAddress,
                LastDeliveryId = previousId,
                LastSentAt = previousTime,
            });
            await fixture.Database.SaveChangesAsync().ConfigureAwait(false);
        }
        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(SenderAddress, AccountAddress,
            DeliverableMessage, DefaultFolders.Inbox, Guid.CreateVersion7()).ConfigureAwait(false));
        Assert.AreEqual(0, fixture.Queue.EnqueueCalls);
        Assert.AreEqual(existingReply ? 1 : 0, fixture.Database.JmapVacationReplies.Local.Count);
        if (existingReply)
        {
            var reply = fixture.Database.JmapVacationReplies.Local.Single();
            Assert.AreEqual(previousId, reply.LastDeliveryId);
            Assert.AreEqual(previousTime, reply.LastSentAt);
        }
    }

    [TestMethod]
    [DataRow(1, null, true, false)]
    [DataRow(0, null, true, true)]
    [DataRow(null, 0, true, false)]
    [DataRow(null, 1, true, true)]
    [DataRow(null, null, false, false)]
    public async Task ResponseRetainsEnablementAndHalfOpenDateWindow(
        int? fromMinutes, int? toMinutes, bool enabled, bool queued)
    {
        await using var fixture = await VacationFixture.CreateAsync(includeAlias: false).ConfigureAwait(false);
        var settings = await fixture.Database.JmapVacationResponses.SingleAsync().ConfigureAwait(false);
        settings.IsEnabled = enabled;
        settings.FromDate = fromMinutes is null ? null : Now.UtcDateTime.AddMinutes(fromMinutes.Value);
        settings.ToDate = toMinutes is null ? null : Now.UtcDateTime.AddMinutes(toMinutes.Value);
        await fixture.Database.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(SenderAddress, AccountAddress,
            DeliverableMessage, DefaultFolders.Inbox, Guid.CreateVersion7()).ConfigureAwait(false));
        Assert.AreEqual(queued ? 1 : 0, fixture.Queue.EnqueueCalls);
        Assert.AreEqual(queued ? 1 : 0, fixture.Database.JmapVacationReplies.Local.Count);
    }

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
    public async Task ResponseReadsExternalVacationBody()
    {
        await using var fixture = await VacationFixture.CreateAsync(includeAlias: false);
        var settings = await fixture.Database.JmapVacationResponses.SingleAsync();
        var marker = fixture.Effects.Mark();
        await fixture.Content.SetAsync(
            settings, "Blob-backed vacation response", null, CancellationToken.None);
        await fixture.Database.SaveChangesAsync();
        await fixture.Effects.CommitAsync(marker);
        Assert.IsNull(settings.TextBody);

        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress,
            AccountAddress,
            "From: sender@example.net\r\nTo: admin@mk8n.com\r\nSubject: Away\r\n\r\nbody\r\n",
            DefaultFolders.Inbox,
            Guid.CreateVersion7()));

        using var response = MimeMessage.Load(new MemoryStream(
            Encoding.Latin1.GetBytes(fixture.Queue.Submission!.RawMessage)));
        Assert.IsNotNull(response.TextBody);
        Assert.AreEqual("Blob-backed vacation response", response.TextBody.TrimEnd('\r', '\n'));
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
        VacationResponder responder,
        VacationResponseContentService content,
        LargeObjectTransactionEffects effects) : IAsyncDisposable
    {
        public EmailDbContext Database { get; } = database;
        public CapturingQueue Queue { get; } = queue;
        public VacationResponder Responder { get; } = responder;
        public VacationResponseContentService Content { get; } = content;
        public LargeObjectTransactionEffects Effects { get; } = effects;

        public static async Task<VacationFixture> CreateAsync(bool includeAlias, int maxMessageSizeBytes = 1024 * 1024)
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
                    MaxMessageSizeBytes = maxMessageSizeBytes,
                    MaxRecipientsPerMessage = 10,
                },
            };
            var store = new InMemoryLargeObjectStore();
            var effects = new LargeObjectTransactionEffects(
                store,
                NullLogger<LargeObjectTransactionEffects>.Instance);
            var content = new VacationResponseContentService(store, effects);
            var responder = new VacationResponder(
                database,
                new EmailService(
                    database,
                    new MailboxMessageContentService(store, effects),
                    effects),
                queue,
                content,
                environment,
                new FixedTimeProvider(Now));
            return new VacationFixture(database, queue, responder, content, effects);
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class CapturingQueue : IMailSubmissionQueue
    {
        public MailSubmission? Submission { get; private set; }
        public int EnqueueCalls { get; private set; }

        public Task<Guid> EnqueueAsync(
            MailSubmission submission,
            CancellationToken cancellationToken = default)
        {
            EnqueueCalls++;
            Submission = submission;
            return Task.FromResult(submission.QueueId);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
