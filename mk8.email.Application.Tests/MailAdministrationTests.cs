using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class MailAdministrationTests
{
    [TestMethod]
    public async Task CatchAllAcceptsAndDeliversAnUndefinedAddress()
    {
        await using var database = CreateDatabase();
        var administration = new MailAdministrationService(database);
        Assert.IsTrue((await administration.EnsureDomainAsync("mk8n", "mk8n.com")).Succeeded);
        Assert.IsTrue((await administration.CreateAccountAsync(
            "admin@mk8n.com",
            "administrator-password-value",
            UserRole.SuperAdmin)).Succeeded);
        Assert.IsTrue((await administration.CreateAccountAsync(
            "mk8n@mk8n.com",
            "mailbox-password-value",
            UserRole.User)).Succeeded);
        Assert.IsTrue((await administration.SetCatchAllAsync(
            "mk8n.com",
            "mk8n@mk8n.com")).Succeeded);
        Assert.IsTrue((await administration.SetDomainActiveAsync("mk8n.com", true)).Succeeded);

        var mail = new EmailService(database);
        Assert.IsTrue(await mail.CanReceiveAsync("undefined@mk8n.com"));
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "undefined@mk8n.com",
            "From: sender@example.net\r\nTo: undefined@mk8n.com\r\nSubject: route test\r\n\r\nbody\r\n"));

        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync();
        var target = await database.Inboxes.SingleAsync(inbox => inbox.Name == "mk8n");
        Assert.AreEqual(target.Id, delivered.Folder.InboxId);
    }

    [TestMethod]
    public async Task ExactAccountTakesPriorityOverCatchAll()
    {
        await using var database = CreateDatabase();
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("mk8n", "mk8n.com");
        await administration.CreateAccountAsync(
            "admin@mk8n.com",
            "administrator-password-value",
            UserRole.SuperAdmin);
        await administration.CreateAccountAsync(
            "mk8n@mk8n.com",
            "mailbox-password-value",
            UserRole.User);
        await administration.SetCatchAllAsync("mk8n.com", "mk8n@mk8n.com");
        await administration.SetDomainActiveAsync("mk8n.com", true);

        var mail = new EmailService(database);
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "admin@mk8n.com",
            "From: sender@example.net\r\nTo: admin@mk8n.com\r\nSubject: exact test\r\n\r\nbody\r\n"));

        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync();
        var target = await database.Inboxes.SingleAsync(inbox => inbox.Name == "admin");
        Assert.AreEqual(target.Id, delivered.Folder.InboxId);
    }

    [TestMethod]
    public async Task DeliveryThreadsRepliesWithinTheDestinationAccount()
    {
        await using var database = CreateDatabase();
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("Example", "example.com");
        await administration.CreateAccountAsync(
            "alice@example.com",
            "alice-password-value",
            UserRole.User);
        await administration.CreateAccountAsync(
            "bob@example.com",
            "bob-password-value",
            UserRole.User);
        await administration.SetDomainActiveAsync("example.com", true);

        const string originalMessageId = "<shared-original@example.net>";
        var mail = new EmailService(database);
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "alice@example.com",
            $"From: sender@example.net\r\nTo: alice@example.com\r\n"
                + $"Message-ID: {originalMessageId}\r\nSubject: original\r\n\r\nbody\r\n"));
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "bob@example.com",
            $"From: sender@example.net\r\nTo: bob@example.com\r\n"
                + $"Message-ID: {originalMessageId}\r\nSubject: original\r\n\r\nbody\r\n"));
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "bob@example.com",
            "From: sender@example.net\r\nTo: bob@example.com\r\n"
                + "Message-ID: <reply@example.net>\r\n"
                + $"In-Reply-To: {originalMessageId}\r\nSubject: Re: original\r\n\r\nreply\r\n"));

        var messages = await database.Emails
            .AsNoTracking()
            .Select(email => new
            {
                Account = email.Folder.Inbox.Name,
                email.MessageId,
                email.ThreadObjectId,
            })
            .ToListAsync();
        var aliceOriginal = messages.Single(message => message.Account == "alice");
        var bobMessages = messages.Where(message => message.Account == "bob").ToArray();
        var bobOriginal = bobMessages.Single(message => message.MessageId == originalMessageId);
        var bobReply = bobMessages.Single(message => message.MessageId == "<reply@example.net>");

        Assert.AreNotEqual(aliceOriginal.ThreadObjectId, bobOriginal.ThreadObjectId);
        Assert.AreEqual(bobOriginal.ThreadObjectId, bobReply.ThreadObjectId);
    }

    [TestMethod]
    public async Task ProvisioningRejectsUnsafeMailboxNames()
    {
        await using var database = CreateDatabase();
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("mk8n", "mk8n.com");

        var result = await administration.CreateAccountAsync(
            "../admin@mk8n.com",
            "administrator-password-value",
            UserRole.SuperAdmin);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, await database.Users.CountAsync());
    }

    [TestMethod]
    public async Task NewDomainCanBeConfiguredButCannotReceiveBeforeActivation()
    {
        await using var database = CreateDatabase();
        var administration = new MailAdministrationService(database);
        var mail = new EmailService(database);

        Assert.IsTrue((await administration.EnsureDomainAsync("Example", "example.com")).Succeeded);
        Assert.IsTrue((await administration.CreateAccountAsync(
            "postmaster@example.com",
            "postmaster-password-value",
            UserRole.User)).Succeeded);
        Assert.IsTrue((await administration.SetCatchAllAsync(
            "example.com",
            "postmaster@example.com")).Succeeded);
        Assert.IsFalse(await mail.CanReceiveAsync("unknown@example.com"));

        Assert.IsTrue((await administration.SetDomainActiveAsync("example.com", true)).Succeeded);
        Assert.IsTrue(await mail.CanReceiveAsync("unknown@example.com"));
    }

    [TestMethod]
    public async Task DomainActivationDoesNotChangeAnotherDomain()
    {
        await using var database = CreateDatabase();
        var administration = new MailAdministrationService(database);
        var mail = new EmailService(database);

        await administration.EnsureDomainAsync("Example", "example.com");
        await administration.CreateAccountAsync(
            "postmaster@example.com",
            "postmaster-password-value",
            UserRole.User);
        await administration.EnsureDomainAsync("Example", "example.net");
        await administration.CreateAccountAsync(
            "postmaster@example.net",
            "postmaster-password-value",
            UserRole.User);

        Assert.IsTrue((await administration.SetDomainActiveAsync("example.com", true)).Succeeded);
        Assert.IsTrue(await mail.CanReceiveAsync("postmaster@example.com"));
        Assert.IsFalse(await mail.CanReceiveAsync("postmaster@example.net"));

        Assert.IsTrue((await administration.SetDomainActiveAsync("example.net", true)).Succeeded);
        Assert.IsTrue((await administration.SetDomainActiveAsync("example.com", false)).Succeeded);
        Assert.IsFalse(await mail.CanReceiveAsync("postmaster@example.com"));
        Assert.IsTrue(await mail.CanReceiveAsync("postmaster@example.net"));
    }

    [TestMethod]
    public async Task DomainDeactivationRevokesJmapPushSubscriptionsForItsAccountsOnly()
    {
        await using var database = CreateDatabase();
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("Example", "example.com");
        await administration.CreateAccountAsync(
            "postmaster@example.com",
            "postmaster-password-value",
            UserRole.User);
        await administration.EnsureDomainAsync("Example", "example.net");
        await administration.CreateAccountAsync(
            "postmaster@example.net",
            "postmaster-password-value",
            UserRole.User);
        await administration.SetDomainActiveAsync("example.com", true);
        await administration.SetDomainActiveAsync("example.net", true);

        var revokedUserId = await database.Users
            .Where(user => user.Username == "postmaster@example.com")
            .Select(user => user.Id)
            .SingleAsync();
        var retainedUserId = await database.Users
            .Where(user => user.Username == "postmaster@example.net")
            .Select(user => user.Id)
            .SingleAsync();
        var revokedSubscription = CreatePushSubscription(revokedUserId, "revoked");
        var retainedSubscription = CreatePushSubscription(retainedUserId, "retained");
        database.JmapPushSubscriptions.AddRange(revokedSubscription, retainedSubscription);
        await database.SaveChangesAsync();

        Assert.IsTrue((await administration.SetDomainActiveAsync("example.com", false)).Succeeded);

        var remaining = await database.JmapPushSubscriptions.AsNoTracking().SingleAsync();
        Assert.AreEqual(retainedSubscription.Id, remaining.Id);
        Assert.AreEqual(string.Empty, revokedSubscription.Url);
        Assert.IsNull(revokedSubscription.KeysJson);
    }

    [TestMethod]
    public async Task UserQuotaIncludesEveryOwnedInbox()
    {
        await using var database = CreateDatabase();
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("Test Company", "mk8n.com");
        await administration.EnsureDomainAsync("Test Company", "example.com");
        await administration.CreateAccountAsync(
            "user@mk8n.com",
            "mailbox-password-value",
            UserRole.User);
        await administration.SetDomainActiveAsync("mk8n.com", true);
        await administration.SetDomainActiveAsync("example.com", true);

        var user = await database.Users.SingleAsync();
        var secondAddress = await database.Addresses.SingleAsync(item => item.Domain == "example.com");
        var primaryFolder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Inbox);
        var secondInbox = new InboxDB
        {
            Id = Guid.CreateVersion7(),
            Name = "user",
            AddressId = secondAddress.Id,
            OwnerId = user.Id,
        };
        database.Inboxes.Add(secondInbox);
        database.Folders.Add(new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = DefaultFolders.Inbox,
            Inbox = secondInbox,
        });

        const string rawMessage =
            "From: sender@example.net\r\n" +
            "To: user@example.com\r\n" +
            "Subject: quota test\r\n\r\n" +
            "body\r\n";
        user.QuotaBytes = rawMessage.Length + 50;
        primaryFolder.NextUid = 2;
        primaryFolder.HighestModSeq = 1;
        database.Emails.Add(new EmailDB
        {
            Id = Guid.CreateVersion7(),
            Sender = "sender@example.net",
            Recipient = "user@mk8n.com",
            Subject = "existing message",
            Body = "body\r\n",
            RawHeaders = "From: sender@example.net\r\nTo: user@mk8n.com\r\nSubject: existing message",
            SizeBytes = 100,
            Uid = 1,
            ModSeq = 1,
            FolderId = primaryFolder.Id,
        });
        await database.SaveChangesAsync();

        var mail = new EmailService(database);
        Assert.IsFalse(await mail.DeliverAsync(
            "sender@example.net",
            "user@example.com",
            rawMessage));
        Assert.AreEqual(1, await database.Emails.CountAsync());
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
