using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
public sealed class OAuthSchemaTests
{
    [TestMethod]
    public void OAuthModelStoresHashedTokensUnderRevocableDeviceGrants()
    {
        using var database = CreateDatabase();
        var grant = database.Model.FindEntityType(typeof(OAuthGrantDB));
        var token = database.Model.FindEntityType(typeof(OAuthTokenDB));

        Assert.IsNotNull(grant);
        Assert.IsNotNull(token);
        Assert.AreEqual("oauth_grants", grant.GetTableName());
        Assert.AreEqual("oauth_tokens", token.GetTableName());
        Assert.AreEqual("text[]", grant.FindProperty(nameof(OAuthGrantDB.Scopes))?.GetColumnType());
        Assert.AreEqual("bytea", token.FindProperty(nameof(OAuthTokenDB.TokenHash))?.GetColumnType());
        Assert.AreEqual(128, grant.FindProperty(nameof(OAuthGrantDB.ClientId))?.GetMaxLength());
        Assert.AreEqual(128, grant.FindProperty(nameof(OAuthGrantDB.DeviceName))?.GetMaxLength());
        Assert.AreEqual(16, token.FindProperty(nameof(OAuthTokenDB.TokenType))?.GetMaxLength());
        Assert.AreEqual(DeleteBehavior.Cascade, grant.GetForeignKeys().Single().DeleteBehavior);
        Assert.AreEqual(DeleteBehavior.Cascade, token.GetForeignKeys().Single().DeleteBehavior);
        Assert.IsTrue(token.GetIndexes().Any(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(OAuthTokenDB.GrantId), nameof(OAuthTokenDB.TokenType)])));
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new EmailDbContext(options);
    }
}
