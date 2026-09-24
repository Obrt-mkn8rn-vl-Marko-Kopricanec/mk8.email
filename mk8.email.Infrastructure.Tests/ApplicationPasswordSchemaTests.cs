using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability", "CA1515",
    Justification = "MSTest discovers this public test class by reflection.")]
public sealed class ApplicationPasswordSchemaTests
{
    [TestMethod]
    public void ModelStoresOnlyHashedRevocableCredentialsPerUser()
    {
        using var database = CreateDatabase();
        var password = database.Model.FindEntityType(typeof(ApplicationPasswordDB));

        Assert.IsNotNull(password);
        Assert.AreEqual("application_passwords", password.GetTableName(), StringComparer.Ordinal);
        Assert.AreEqual(128, password.FindProperty(nameof(ApplicationPasswordDB.Name))?.GetMaxLength());
        Assert.AreEqual(255, password.FindProperty(nameof(ApplicationPasswordDB.PasswordHash))?.GetMaxLength());
        Assert.IsNotNull(password.FindProperty(nameof(ApplicationPasswordDB.LastUsedAt)));
        Assert.IsNotNull(password.FindProperty(nameof(ApplicationPasswordDB.RevokedAt)));
        Assert.AreEqual(DeleteBehavior.Cascade, password.GetForeignKeys().Single().DeleteBehavior);
        Assert.IsTrue(password.GetIndexes().Any(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(ApplicationPasswordDB.UserId), nameof(ApplicationPasswordDB.Name)], StringComparer.Ordinal)));
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new EmailDbContext(options);
    }
}
