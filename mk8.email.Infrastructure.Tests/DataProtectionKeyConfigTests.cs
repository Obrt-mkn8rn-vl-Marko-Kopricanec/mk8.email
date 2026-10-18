using System.Security.Cryptography;
using mk8.email.Configuration;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "MSTest discovers this public configuration fixture by reflection.")]
public sealed class DataProtectionKeyConfigTests
{
    [TestMethod]
    public void DecodingReturnsIndependentOwnedBytes()
    {
        var key = PortableCookieKeyFixture.Create();
        var first = key.DecodeMasterKey();
        try
        {
            var second = key.DecodeMasterKey();
            try
            {
                Assert.AreNotSame(first, second);
                Assert.AreSequenceEqual(first, second);
                first[0] ^= 1;
                Assert.AreNotEqual(first[0], second[0]);
                Assert.AreEqual(key.MasterKey, Convert.ToBase64String(second), StringComparer.Ordinal);
            }
            finally { CryptographicOperations.ZeroMemory(second); }
        }
        finally { CryptographicOperations.ZeroMemory(first); }
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("duplicate")]
    [DataRow("zero-id")]
    [DataRow("expired")]
    [DataRow("future-only")]
    [DataRow("malformed")]
    [DataRow("revoked-only")]
    public void InvalidKeyringsFailClosedWithoutSecretDisclosure(string invalid)
    {
        var valid = PortableCookieKeyFixture.Create();
        var key = new DataProtectionKeyConfig
        {
            Id = string.Equals(invalid, "zero-id", StringComparison.Ordinal) ? Guid.Empty : valid.Id,
            CreatedAt = valid.CreatedAt,
            ActivatesAt = string.Equals(invalid, "future-only", StringComparison.Ordinal) ? DateTimeOffset.UtcNow.AddHours(1) : valid.ActivatesAt,
            ExpiresAt = string.Equals(invalid, "expired", StringComparison.Ordinal) ? DateTimeOffset.UtcNow.AddHours(-1) : valid.ExpiresAt,
            RevokedAt = string.Equals(invalid, "revoked-only", StringComparison.Ordinal) ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
            MasterKey = string.Equals(invalid, "malformed", StringComparison.Ordinal) ? "never-log-this-invalid-master-key" : valid.MasterKey,
        };
        DataProtectionKeyConfig[] keys = invalid switch
        {
            "empty" => [],
            "duplicate" => [key, key],
            _ => [key],
        };
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => DataProtectionKeyConfig.ValidateKeys(keys));
        Assert.IsNull(failure.InnerException);
        Assert.DoesNotContain(key.MasterKey, failure.Message, StringComparison.Ordinal);
    }
}
