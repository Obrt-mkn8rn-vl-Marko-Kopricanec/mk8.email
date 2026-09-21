using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
public sealed class MfaSchemaTests
{
    [TestMethod]
    public void MfaModelStoresEncryptedSecretsAndHashedRecoveryCodes()
    {
        using var database = CreateDatabase();
        var credential = database.Model.FindEntityType(typeof(MfaTotpCredentialDB));
        var recoveryCode = database.Model.FindEntityType(typeof(MfaRecoveryCodeDB));

        Assert.IsNotNull(credential);
        Assert.IsNotNull(recoveryCode);
        Assert.AreEqual("mfa_totp_credentials", credential.GetTableName());
        Assert.AreEqual("mfa_recovery_codes", recoveryCode.GetTableName());
        Assert.AreEqual(
            "bytea",
            credential.FindProperty(nameof(MfaTotpCredentialDB.EncryptedSecret))?.GetColumnType());
        Assert.AreEqual(
            "bytea",
            credential.FindProperty(nameof(MfaTotpCredentialDB.EncryptionNonce))?.GetColumnType());
        Assert.AreEqual(
            "bytea",
            credential.FindProperty(nameof(MfaTotpCredentialDB.EncryptionTag))?.GetColumnType());
        Assert.AreEqual(
            "bytea",
            recoveryCode.FindProperty(nameof(MfaRecoveryCodeDB.CodeHash))?.GetColumnType());
        Assert.IsTrue(credential.GetIndexes().Single(index =>
            index.Properties.Single().Name == nameof(MfaTotpCredentialDB.UserId)).IsUnique);
        Assert.AreEqual(DeleteBehavior.Cascade, credential.GetForeignKeys().Single().DeleteBehavior);
        Assert.AreEqual(DeleteBehavior.Cascade, recoveryCode.GetForeignKeys().Single().DeleteBehavior);
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new EmailDbContext(options);
    }
}
