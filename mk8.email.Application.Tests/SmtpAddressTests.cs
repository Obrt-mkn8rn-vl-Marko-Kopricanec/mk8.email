using mk8.email.Application.Protocol;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class SmtpAddressTests
{
    [TestMethod]
    public void InternationalizedMailboxIsNormalizedAndIdnDomainUsesALabel()
    {
        Assert.IsTrue(SmtpAddress.TryNormalize(
            "Usér@BÜCHER.example",
            allowEmpty: false,
            out var address,
            out var requiresSmtpUtf8));

        Assert.AreEqual("usér@xn--bcher-kva.example", address);
        Assert.IsTrue(requiresSmtpUtf8);
    }

    [TestMethod]
    public void QuotedLocalPartSupportsSmtpMailboxSyntax()
    {
        Assert.IsTrue(SmtpAddress.TryNormalize(
            "\"Customer Care\"@Example.COM",
            allowEmpty: false,
            out var address,
            out var requiresSmtpUtf8));

        Assert.AreEqual("\"customer care\"@example.com", address);
        Assert.IsFalse(requiresSmtpUtf8);
    }

    [TestMethod]
    public void InvalidAndOversizedInternationalizedLocalPartsAreRejected()
    {
        Assert.IsFalse(SmtpAddress.TryNormalize(
            $"{new string('é', 33)}@example.com",
            allowEmpty: false,
            out _));
        Assert.IsFalse(SmtpAddress.TryNormalize(
            "\"unterminated@example.com",
            allowEmpty: false,
            out _));
        Assert.IsFalse(SmtpAddress.TryNormalize(
            "bad local@example.com",
            allowEmpty: false,
            out _));
    }
}
