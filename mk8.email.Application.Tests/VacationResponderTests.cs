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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class VacationResponderTests
{
    private const string AccountAddress = "admin@tenant.example.test";
    private const string AliasAddress = "support@tenant.example.test";
    private const string SenderAddress = "sender@example.net";
    private const string DeliverableMessage =
        "From: sender@example.net\r\nTo: admin@tenant.example.test\r\nSubject: Away\r\n\r\nbody\r\n";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(8, true, false)]
    [DataRow(6, false, false)]
    [DataRow(7, false, true)]
    [DataRow(8, false, true)]
    public async Task RepeatSuppressionRetainsDeliveryAndSevenDayBoundaries(int days, bool replay, bool queued)
    {
        var fixture = (await VacationFixture.CreateAsync(includeAlias: false).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        await (fixture.Database.JmapVacationReplies.AddAsync(reply)).ConfigureAwait(false);
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
        var fixture = (await VacationFixture.CreateAsync(
            includeAlias: false, maxMessageSizeBytes: 1).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var accountId = (await fixture.Database.JmapVacationResponses.SingleAsync().ConfigureAwait(false)).AccountId;
        var previousId = Guid.CreateVersion7();
        var previousTime = Now.UtcDateTime.AddDays(-8);
        if (existingReply)
        {
            await (fixture.Database.JmapVacationReplies.AddAsync(new JmapVacationReplyDB
            {
                Id = Guid.CreateVersion7(),
                AccountId = accountId,
                SenderAddress = SenderAddress,
                LastDeliveryId = previousId,
                LastSentAt = previousTime,
            })).ConfigureAwait(false);
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
        var fixture = (await VacationFixture.CreateAsync(includeAlias: false).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        var fixture = (await VacationFixture.CreateAsync(includeAlias: false).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
            Guid.CreateVersion7()).ConfigureAwait(false));

        Assert.IsNull(fixture.Queue.Submission);
        Assert.AreEqual(0, await fixture.Database.JmapVacationReplies.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ResponseRecognizesDeliveredAliasesInResentRecipientHeaders()
    {
        var fixture = (await VacationFixture.CreateAsync(includeAlias: true).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var rawMessage =
            "From: sender@example.net\r\n" +
            "To: another@example.org\r\n" +
            "Resent-Cc: Support <support@tenant.example.test>\r\n" +
            "References: <root@example.net> <ancestor@example.net>\r\n" +
            "Message-ID: <original@example.net>\r\n" +
            "Subject: alias check\r\n\r\n" +
            "body\r\n";

        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress,
            AliasAddress,
            rawMessage,
            DefaultFolders.Inbox,
            Guid.CreateVersion7()).ConfigureAwait(false));

        var submission = fixture.Queue.Submission;
        Assert.IsNotNull(submission);
        Assert.AreEqual(string.Empty, submission.EnvelopeSender, StringComparer.Ordinal);
        Assert.AreEqual(SenderAddress, submission.Recipients.Single().Address, StringComparer.Ordinal);
        Assert.IsFalse(submission.Recipients.Single().IsLocal);

        using var response = await MimeMessage.LoadAsync(
            new MemoryStream(Encoding.Latin1.GetBytes(submission.RawMessage))).ConfigureAwait(false);
        Assert.AreEqual(AccountAddress, response.From.Mailboxes.Single().Address, StringComparer.Ordinal);
        Assert.AreEqual("auto-replied", response.Headers[HeaderId.AutoSubmitted], StringComparer.Ordinal);
        Assert.AreEqual("Auto: alias check", response.Subject, StringComparer.Ordinal);
        Assert.AreEqual("original@example.net", response.InReplyTo, StringComparer.Ordinal);
        CollectionAssert.AreEqual(
            ExpectedVector1,
            response.References.ToArray());
        Assert.AreEqual(1, fixture.Database.JmapVacationReplies.Local.Count);
    }

    [TestMethod]
    public async Task ResponseBuildsReferencesFromInReplyToWhenNoReferencesArePresent()
    {
        var fixture = (await VacationFixture.CreateAsync(includeAlias: false).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var rawMessage =
            "From: sender@example.net\r\n" +
            "To: admin@tenant.example.test\r\n" +
            "In-Reply-To: <root@example.net>\r\n" +
            "Message-ID: <original@example.net>\r\n" +
            "Subject: thread check\r\n\r\n" +
            "body\r\n";

        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress,
            AccountAddress,
            rawMessage,
            DefaultFolders.Inbox,
            Guid.CreateVersion7()).ConfigureAwait(false));

        using var response = await MimeMessage.LoadAsync(new MemoryStream(
            Encoding.Latin1.GetBytes(fixture.Queue.Submission!.RawMessage))).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            ExpectedVector2,
            response.References.ToArray());
    }

    [TestMethod]
    public async Task ResponseReadsExternalVacationBody()
    {
        var fixture = (await VacationFixture.CreateAsync(includeAlias: false).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var settings = await fixture.Database.JmapVacationResponses.SingleAsync().ConfigureAwait(false);
        var marker = fixture.Effects.Mark();
        await fixture.Content.SetAsync(
            settings, "Blob-backed vacation response", null, CancellationToken.None).ConfigureAwait(false);
        await fixture.Database.SaveChangesAsync().ConfigureAwait(false);
        await fixture.Effects.CommitAsync(marker).ConfigureAwait(false);
        Assert.IsNull(settings.TextBody);

        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress,
            AccountAddress,
            "From: sender@example.net\r\nTo: admin@tenant.example.test\r\nSubject: Away\r\n\r\nbody\r\n",
            DefaultFolders.Inbox,
            Guid.CreateVersion7()).ConfigureAwait(false));

        using var response = await MimeMessage.LoadAsync(new MemoryStream(
            Encoding.Latin1.GetBytes(fixture.Queue.Submission!.RawMessage))).ConfigureAwait(false);
        Assert.IsNotNull(response.TextBody);
        Assert.AreEqual("Blob-backed vacation response", response.TextBody.TrimEnd('\r', '\n'), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ParameterizedManualAutoSubmittedHeaderStillReceivesAResponse()
    {
        var fixture = (await VacationFixture.CreateAsync(includeAlias: false).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var rawMessage =
            "From: sender@example.net\r\n" +
            "To: admin@tenant.example.test\r\n" +
            "Auto-Submitted: (origin) no (manual); x-client=\"mail app\"\r\n" +
            "Subject: parameter check\r\n\r\n" +
            "body\r\n";

        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress,
            AccountAddress,
            rawMessage,
            DefaultFolders.Inbox,
            Guid.CreateVersion7()).ConfigureAwait(false));

        Assert.IsNotNull(fixture.Queue.Submission);
    }

    [TestMethod]
    public async Task AnyAutomaticAutoSubmittedHeaderSuppressesAResponse()
    {
        var fixture = (await VacationFixture.CreateAsync(includeAlias: false).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var rawMessage =
            "From: sender@example.net\r\n" +
            "To: admin@tenant.example.test\r\n" +
            "Auto-Submitted: no\r\n" +
            "Auto-Submitted: auto-generated; x-source=scheduler\r\n" +
            "Subject: loop check\r\n\r\n" +
            "body\r\n";

        Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
            SenderAddress,
            AccountAddress,
            rawMessage,
            DefaultFolders.Inbox,
            Guid.CreateVersion7()).ConfigureAwait(false));

        Assert.IsNull(fixture.Queue.Submission);
        Assert.AreEqual(0, await fixture.Database.JmapVacationReplies.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RepeatSuppressionUsesCultureInvariantSenderKeys()
    {
        var fixture = (await VacationFixture.CreateAsync(includeAlias: true).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        const string sender = "INFO@example.net";
        var rawMessage =
            "From: INFO@example.net\r\n" +
            "To: support@tenant.example.test\r\n" +
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
                Guid.CreateVersion7()).ConfigureAwait(false));
            Assert.IsNotNull(fixture.Queue.Submission);
            await fixture.Database.SaveChangesAsync().ConfigureAwait(false);
            Assert.IsTrue(await fixture.Responder.QueueResponseAsync(
                sender,
                AliasAddress,
                rawMessage,
                DefaultFolders.Inbox,
                Guid.CreateVersion7()).ConfigureAwait(false));
            await fixture.Database.SaveChangesAsync().ConfigureAwait(false);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.AreEqual(1, await fixture.Database.JmapVacationReplies.CountAsync().ConfigureAwait(false));
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

            // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
            EmailDbContext? database = new EmailDbContext(options);

#pragma warning restore CA2000

            try
            {
                await SeedAsync(database, includeAlias).ConfigureAwait(false);
                var fixture = BuildFixture(database, maxMessageSizeBytes);
                database = null;
                return fixture;
            }
            finally
            {

                // Successful transfer clears the resource; initialization exceptions leave it non-null for finally cleanup.
#pragma warning disable CA1508
                if (database is not null) await database.DisposeAsync().ConfigureAwait(false);

#pragma warning restore CA1508
            }
        }

        private static async Task SeedAsync(EmailDbContext database, bool includeAlias)
        {
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);

            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "Vacation Test",
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
            await database.Inboxes.AddAsync(account).ConfigureAwait(false);
            if (includeAlias)
            {
                await database.Inboxes.AddAsync(new InboxDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "support",
                    Address = address,
                    Owner = user,
                    AliasForInbox = account,
                }).ConfigureAwait(false);
            }
            await database.JmapVacationResponses.AddAsync(new JmapVacationResponseDB
            {
                AccountId = account.Id,
                IsEnabled = true,
                TextBody = "I am away.",
            }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);


        }

        private static VacationFixture BuildFixture(EmailDbContext database, int maxMessageSizeBytes)
        {
            var queue = new CapturingQueue();
            var environment = new EnvironmentConfig
            {
                Smtp = new SmtpConfig { Hostname = "email.tenant.example.test" },
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
    private static readonly string[] ExpectedVector1 = new[] { "root@example.net", "ancestor@example.net", "original@example.net" };
    private static readonly string[] ExpectedVector2 = new[] { "root@example.net", "original@example.net" };
}
