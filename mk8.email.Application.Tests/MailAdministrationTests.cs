using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class MailAdministrationTests
{
    [TestMethod]
    public async Task CatchAllAcceptsAndDeliversAnUndefinedAddress()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var administration = new MailAdministrationService(database);
        Assert.IsTrue((await administration.EnsureDomainAsync("mk8n", "tenant.example.test").ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await administration.CreateAccountAsync(
            "admin@tenant.example.test",
            "administrator-password-value",
            UserRole.SuperAdmin).ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await administration.CreateAccountAsync(
            "mk8n@tenant.example.test",
            "mailbox-password-value",
            UserRole.User).ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await administration.SetCatchAllAsync(
            "tenant.example.test",
            "mk8n@tenant.example.test").ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await administration.SetDomainActiveAsync("tenant.example.test", true).ConfigureAwait(false)).Succeeded);

        var mail = CreateEmailService(database);
        Assert.IsTrue(await mail.CanReceiveAsync("undefined@tenant.example.test").ConfigureAwait(false));
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "undefined@tenant.example.test",
            "From: sender@example.net\r\nTo: undefined@tenant.example.test\r\nSubject: route test\r\n\r\nbody\r\n").ConfigureAwait(false));

        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync().ConfigureAwait(false);
        var target = await database.Inboxes.SingleAsync(inbox => inbox.Name == "mk8n").ConfigureAwait(false);
        Assert.AreEqual(target.Id, delivered.Folder.InboxId);
    }

    [TestMethod]
    public async Task ExactAccountTakesPriorityOverCatchAll()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("mk8n", "tenant.example.test").ConfigureAwait(false);
        await administration.CreateAccountAsync(
            "admin@tenant.example.test",
            "administrator-password-value",
            UserRole.SuperAdmin).ConfigureAwait(false);
        await administration.CreateAccountAsync(
            "mk8n@tenant.example.test",
            "mailbox-password-value",
            UserRole.User).ConfigureAwait(false);
        await administration.SetCatchAllAsync("tenant.example.test", "mk8n@tenant.example.test").ConfigureAwait(false);
        await administration.SetDomainActiveAsync("tenant.example.test", true).ConfigureAwait(false);

        var mail = CreateEmailService(database);
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "admin@tenant.example.test",
            "From: sender@example.net\r\nTo: admin@tenant.example.test\r\nSubject: exact test\r\n\r\nbody\r\n").ConfigureAwait(false));

        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync().ConfigureAwait(false);
        var target = await database.Inboxes.SingleAsync(inbox => inbox.Name == "admin").ConfigureAwait(false);
        Assert.AreEqual(target.Id, delivered.Folder.InboxId);
    }

    [TestMethod]
    public async Task DeliveryThreadsRepliesWithinTheDestinationAccount()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("Example", "example.com").ConfigureAwait(false);
        await administration.CreateAccountAsync(
            "alice@example.com",
            "alice-password-value",
            UserRole.User).ConfigureAwait(false);
        await administration.CreateAccountAsync(
            "bob@example.com",
            "bob-password-value",
            UserRole.User).ConfigureAwait(false);
        await administration.SetDomainActiveAsync("example.com", true).ConfigureAwait(false);

        const string originalMessageId = "<shared-original@example.net>";
        var mail = CreateEmailService(database);
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "alice@example.com",
            $"From: sender@example.net\r\nTo: alice@example.com\r\n"
                + $"Message-ID: {originalMessageId}\r\nSubject: original\r\n\r\nbody\r\n").ConfigureAwait(false));
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "bob@example.com",
            $"From: sender@example.net\r\nTo: bob@example.com\r\n"
                + $"Message-ID: {originalMessageId}\r\nSubject: original\r\n\r\nbody\r\n").ConfigureAwait(false));
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "bob@example.com",
            "From: sender@example.net\r\nTo: bob@example.com\r\n"
                + "Message-ID: <reply@example.net>\r\n"
                + $"In-Reply-To: {originalMessageId}\r\nSubject: Re: original\r\n\r\nreply\r\n").ConfigureAwait(false));

        var messages = await database.Emails
            .AsNoTracking()
            .Select(email => new
            {
                Account = email.Folder.Inbox.Name,
                email.MessageId,
                email.ThreadObjectId,
            })
            .ToListAsync().ConfigureAwait(false);
        var aliceOriginal = messages.Single(message => string.Equals(message.Account, "alice", StringComparison.Ordinal));
        var bobMessages = messages.Where(message => string.Equals(message.Account, "bob", StringComparison.Ordinal)).ToArray();
        var bobOriginal = bobMessages.Single(message => string.Equals(message.MessageId, originalMessageId, StringComparison.Ordinal));
        var bobReply = bobMessages.Single(message => string.Equals(message.MessageId, "<reply@example.net>", StringComparison.Ordinal));

        Assert.AreNotEqual(aliceOriginal.ThreadObjectId, bobOriginal.ThreadObjectId, StringComparer.Ordinal);
        Assert.AreEqual(bobOriginal.ThreadObjectId, bobReply.ThreadObjectId, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ProvisioningRejectsUnsafeMailboxNames()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("mk8n", "tenant.example.test").ConfigureAwait(false);

        var result = await administration.CreateAccountAsync(
            "../admin@tenant.example.test",
            "administrator-password-value",
            UserRole.SuperAdmin).ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, await database.Users.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task NewDomainCanBeConfiguredButCannotReceiveBeforeActivation()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var administration = new MailAdministrationService(database);
        var mail = CreateEmailService(database);

        Assert.IsTrue((await administration.EnsureDomainAsync("Example", "example.com").ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await administration.CreateAccountAsync(
            "postmaster@example.com",
            "postmaster-password-value",
            UserRole.User).ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await administration.SetCatchAllAsync(
            "example.com",
            "postmaster@example.com").ConfigureAwait(false)).Succeeded);
        Assert.IsFalse(await mail.CanReceiveAsync("unknown@example.com").ConfigureAwait(false));

        Assert.IsTrue((await administration.SetDomainActiveAsync("example.com", true).ConfigureAwait(false)).Succeeded);
        Assert.IsTrue(await mail.CanReceiveAsync("unknown@example.com").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DomainActivationDoesNotChangeAnotherDomain()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var administration = new MailAdministrationService(database);
        var mail = CreateEmailService(database);

        await administration.EnsureDomainAsync("Example", "example.com").ConfigureAwait(false);
        await administration.CreateAccountAsync(
            "postmaster@example.com",
            "postmaster-password-value",
            UserRole.User).ConfigureAwait(false);
        await administration.EnsureDomainAsync("Example", "example.net").ConfigureAwait(false);
        await administration.CreateAccountAsync(
            "postmaster@example.net",
            "postmaster-password-value",
            UserRole.User).ConfigureAwait(false);

        Assert.IsTrue((await administration.SetDomainActiveAsync("example.com", true).ConfigureAwait(false)).Succeeded);
        Assert.IsTrue(await mail.CanReceiveAsync("postmaster@example.com").ConfigureAwait(false));
        Assert.IsFalse(await mail.CanReceiveAsync("postmaster@example.net").ConfigureAwait(false));

        Assert.IsTrue((await administration.SetDomainActiveAsync("example.net", true).ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await administration.SetDomainActiveAsync("example.com", false).ConfigureAwait(false)).Succeeded);
        Assert.IsFalse(await mail.CanReceiveAsync("postmaster@example.com").ConfigureAwait(false));
        Assert.IsTrue(await mail.CanReceiveAsync("postmaster@example.net").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DomainDeactivationRevokesJmapPushSubscriptionsForItsAccountsOnly()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("Example", "example.com").ConfigureAwait(false);
        await administration.CreateAccountAsync(
            "postmaster@example.com",
            "postmaster-password-value",
            UserRole.User).ConfigureAwait(false);
        await administration.EnsureDomainAsync("Example", "example.net").ConfigureAwait(false);
        await administration.CreateAccountAsync(
            "postmaster@example.net",
            "postmaster-password-value",
            UserRole.User).ConfigureAwait(false);
        await administration.SetDomainActiveAsync("example.com", true).ConfigureAwait(false);
        await administration.SetDomainActiveAsync("example.net", true).ConfigureAwait(false);

        var revokedUserId = await database.Users
            .Where(user => user.Username == "postmaster@example.com")
            .Select(user => user.Id)
            .SingleAsync().ConfigureAwait(false);
        var retainedUserId = await database.Users
            .Where(user => user.Username == "postmaster@example.net")
            .Select(user => user.Id)
            .SingleAsync().ConfigureAwait(false);
        var revokedSubscription = CreatePushSubscription(revokedUserId, "revoked");
        var retainedSubscription = CreatePushSubscription(retainedUserId, "retained");
        await (database.JmapPushSubscriptions.AddRangeAsync(revokedSubscription, retainedSubscription)).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);

        Assert.IsTrue((await administration.SetDomainActiveAsync("example.com", false).ConfigureAwait(false)).Succeeded);

        var remaining = await database.JmapPushSubscriptions.AsNoTracking().SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(retainedSubscription.Id, remaining.Id);
        Assert.AreEqual(string.Empty, revokedSubscription.Url, StringComparer.Ordinal);
        Assert.IsNull(revokedSubscription.KeysJson);
    }

    [TestMethod]
    public async Task UserQuotaIncludesEveryOwnedInbox()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("Test Company", "tenant.example.test").ConfigureAwait(false);
        await administration.EnsureDomainAsync("Test Company", "example.com").ConfigureAwait(false);
        await administration.CreateAccountAsync(
            "user@tenant.example.test",
            "mailbox-password-value",
            UserRole.User).ConfigureAwait(false);
        await administration.SetDomainActiveAsync("tenant.example.test", true).ConfigureAwait(false);
        await administration.SetDomainActiveAsync("example.com", true).ConfigureAwait(false);

        var user = await database.Users.SingleAsync().ConfigureAwait(false);
        var secondAddress = await database.Addresses.SingleAsync(item => item.Domain == "example.com").ConfigureAwait(false);
        var primaryFolder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Inbox).ConfigureAwait(false);
        var secondInbox = new InboxDB
        {
            Id = Guid.CreateVersion7(),
            Name = "user",
            AddressId = secondAddress.Id,
            OwnerId = user.Id,
        };
        await (database.Inboxes.AddAsync(secondInbox)).ConfigureAwait(false);
        await (database.Folders.AddAsync(new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = DefaultFolders.Inbox,
            Inbox = secondInbox,
        })).ConfigureAwait(false);

        const string rawMessage =
            "From: sender@example.net\r\n" +
            "To: user@example.com\r\n" +
            "Subject: quota test\r\n\r\n" +
            "body\r\n";
        user.QuotaBytes = rawMessage.Length + 50;
        primaryFolder.NextUid = 2;
        primaryFolder.HighestModSeq = 1;
        await (database.Emails.AddAsync(new EmailDB
        {
            Id = Guid.CreateVersion7(),
            Sender = "sender@example.net",
            Recipient = "user@tenant.example.test",
            Subject = "existing message",
            Body = "body\r\n",
            RawHeaders = "From: sender@example.net\r\nTo: user@tenant.example.test\r\nSubject: existing message",
            SizeBytes = 100,
            Uid = 1,
            ModSeq = 1,
            FolderId = primaryFolder.Id,
        })).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);

        var mail = CreateEmailService(database);
        Assert.IsFalse(await mail.DeliverAsync(
            "sender@example.net",
            "user@example.com",
            rawMessage).ConfigureAwait(false));
        Assert.AreEqual(1, await database.Emails.CountAsync().ConfigureAwait(false));
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var database = new EmailDbContext(options);
        database.Database.EnsureCreated();
        return database;
    }

    private static EmailService CreateEmailService(EmailDbContext database)
    {
        var store = new InMemoryLargeObjectStore();
        var effects = new LargeObjectTransactionEffects(
            store,
            NullLogger<LargeObjectTransactionEffects>.Instance);
        return new EmailService(
            database,
            new MailboxMessageContentService(store, effects),
            effects);
    }

    private static JmapPushSubscriptionDB CreatePushSubscription(Guid userId, string deviceClientId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            SubscriptionObjectId = $"ps-{Guid.NewGuid():N}",
            UserId = userId,
            DeviceClientId = deviceClientId,
            Url = $"https://push.example/{deviceClientId}",
            KeysJson = "{\"p256dh\":\"key\",\"auth\":\"secret\"}",
            VerificationCode = "verification-code",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };
}
