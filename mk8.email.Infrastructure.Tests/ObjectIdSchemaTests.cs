using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
public sealed class ObjectIdSchemaTests
{
    [TestMethod]
    public void ObjectIdentifiersAreDurableAndIndexed()
    {
        using var database = CreateDatabase();
        var folder = database.Model.FindEntityType(typeof(FolderDB));
        var email = database.Model.FindEntityType(typeof(EmailDB));

        Assert.IsNotNull(folder);
        Assert.IsNotNull(email);

        var mailboxId = folder.FindProperty(nameof(FolderDB.MailboxId));
        Assert.IsNotNull(mailboxId);
        Assert.IsFalse(mailboxId.IsNullable);
        Assert.AreEqual(64, mailboxId.GetMaxLength());
        Assert.IsTrue(folder.GetIndexes().Any(index =>
            index.IsUnique
            && index.Properties.Count == 1
            && index.Properties[0] == mailboxId));

        var emailId = email.FindProperty(nameof(EmailDB.EmailObjectId));
        var threadId = email.FindProperty(nameof(EmailDB.ThreadObjectId));
        Assert.IsNotNull(emailId);
        Assert.IsNotNull(threadId);
        Assert.IsFalse(emailId.IsNullable);
        Assert.IsTrue(threadId.IsNullable);
        Assert.AreEqual(64, emailId.GetMaxLength());
        Assert.AreEqual(64, threadId.GetMaxLength());
        Assert.IsTrue(email.GetIndexes().Any(index =>
            index.Properties.Count == 1
            && index.Properties[0] == emailId));
        Assert.IsTrue(email.GetIndexes().Any(index =>
            index.Properties.Count == 1
            && index.Properties[0] == threadId));
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new EmailDbContext(options);
    }
}
