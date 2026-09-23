using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
public sealed class SieveSchemaTests
{
    [TestMethod]
    public void SieveModelEnforcesPerUserNamesAndSingleActiveScript()
    {
        using var database = CreateDatabase();
        var script = database.Model.FindEntityType(typeof(SieveScriptDB));
        var recipient = database.Model.FindEntityType(typeof(MailQueueRecipientDB));

        Assert.IsNotNull(script);
        Assert.IsNotNull(recipient);
        Assert.AreEqual("sieve_scripts", script.GetTableName());
        Assert.AreEqual(512, script.FindProperty(nameof(SieveScriptDB.Name))?.GetMaxLength());
        Assert.IsTrue(script.FindProperty(nameof(SieveScriptDB.Content))!.IsNullable);
        Assert.AreEqual("size_bytes", script.FindProperty(nameof(SieveScriptDB.SizeBytes))?.GetColumnName());
        Assert.AreEqual(32, script.FindProperty(nameof(SieveScriptDB.ObjectProvider))?.GetMaxLength());
        Assert.AreEqual(1024, script.FindProperty(nameof(SieveScriptDB.ObjectName))?.GetMaxLength());
        var nameIndex = script.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(SieveScriptDB.UserId), nameof(SieveScriptDB.Name)]));
        Assert.IsTrue(nameIndex.IsUnique);
        var activeIndex = script.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(SieveScriptDB.UserId)]));
        Assert.IsTrue(activeIndex.IsUnique);
        Assert.AreEqual("is_active", activeIndex.GetFilter());
        Assert.AreEqual(DeleteBehavior.Cascade, script.GetForeignKeys().Single().DeleteBehavior);
        Assert.AreEqual(
            "text[]",
            recipient.FindProperty(nameof(MailQueueRecipientDB.RedirectHistory))?.GetColumnType());
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new EmailDbContext(options);
    }
}
