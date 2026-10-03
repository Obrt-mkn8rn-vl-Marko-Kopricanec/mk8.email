using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class PasswordHasherTests
{
    [TestMethod]
    public void HashUsesTaggedBcrypt()
    {
        const string password = "correct-horse-battery-staple";

        var hash = PasswordHasher.Hash(password);

        StringAssert.StartsWith(hash, PasswordHasher.BcryptSchemePrefix, StringComparison.Ordinal);
        Assert.IsTrue(PasswordHasher.Verify(password, hash));
        Assert.IsFalse(PasswordHasher.Verify("different-password", hash));
        Assert.IsFalse(PasswordHasher.Verify(password, "invalid-hash"));
    }
}
