using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
public sealed class MailQueueSchemaTests
{
    [TestMethod]
    public void DeliveryStatusFieldsUseBoundedDurableColumns()
    {
        using var database = CreateDatabase();
        var message = database.Model.FindEntityType(typeof(MailQueueMessageDB));
        var recipient = database.Model.FindEntityType(typeof(MailQueueRecipientDB));

        Assert.IsNotNull(message);
        Assert.IsNotNull(recipient);
        Assert.AreEqual(
            "dsn_return_content",
            message.FindProperty(nameof(MailQueueMessageDB.DsnReturnContent))?.GetColumnName());
        Assert.AreEqual(
            100,
            message.FindProperty(nameof(MailQueueMessageDB.DsnEnvelopeId))?.GetMaxLength());
        Assert.AreEqual(
            28,
            recipient.FindProperty(nameof(MailQueueRecipientDB.DsnNotify))?.GetMaxLength());
        Assert.AreEqual(
            500,
            recipient.FindProperty(nameof(MailQueueRecipientDB.DsnOriginalRecipient))?.GetMaxLength());
        Assert.AreEqual(
            16,
            recipient.FindProperty(nameof(MailQueueRecipientDB.LastEnhancedStatusCode))?.GetMaxLength());
        Assert.AreEqual(
            255,
            recipient.FindProperty(nameof(MailQueueRecipientDB.LastRemoteMta))?.GetMaxLength());
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new EmailDbContext(options);
    }
}
