using mk8.email.Configuration;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "MSTest discovers this public test class by reflection.")]
public sealed class SmtpListenAddressTests
{
    [TestMethod]
    public void DefaultBindRetainsIpv4Wildcard() => Assert.AreEqual("0.0.0.0", new SmtpConfig().ListenAddress, StringComparer.Ordinal);

    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("0.0.0.0")]
    [DataRow("::1")]
    [DataRow("::")]
    public void PresentationAdmitsLiteralBindAddresses(string address)
    {
        var config = new EnvironmentConfig { Smtp = new SmtpConfig { ListenAddress = address } };
        var errors = config.Validate(isDevelopment: true, role: EnvironmentValidationRole.Gateway);
        Assert.DoesNotContain(error => error.StartsWith("Smtp.ListenAddress ", StringComparison.Ordinal), errors);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("smtp.example.test")]
    [DataRow("127.0.0.1:2525")]
    public void PresentationRefusesInvalidOrNameBasedBind(string address)
    {
        var config = new EnvironmentConfig { Smtp = new SmtpConfig { ListenAddress = address } };
        var errors = config.Validate(isDevelopment: true, role: EnvironmentValidationRole.Gateway);
        Assert.Contains("Smtp.ListenAddress must be a literal IP address.", errors, StringComparer.Ordinal);
        Assert.DoesNotContain(error => error.StartsWith("Smtp.ListenAddress ", StringComparison.Ordinal),
            config.Validate(isDevelopment: true, role: EnvironmentValidationRole.ApplicationWorker));
    }
}
