using mk8.email.Configuration;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "MSTest discovers this public refusal fixture by reflection.")]
public sealed class LegacyEnvironmentLoaderTests
{
    [TestMethod]
    [DataRow(false, EnvironmentValidationRole.Combined)]
    [DataRow(true, EnvironmentValidationRole.Combined)]
    [DataRow(false, EnvironmentValidationRole.Gateway)]
    [DataRow(false, EnvironmentValidationRole.ApplicationWorker)]
    public void LegacyEntryPointsRefuseBeforeAmbientSelectionOrPathParsing(bool development, EnvironmentValidationRole role)
    {
        var prior = Environment.GetEnvironmentVariable(EnvironmentLoader.ConfigPathVariable);
        try
        {
            Environment.SetEnvironmentVariable(EnvironmentLoader.ConfigPathVariable, "synthetic-never-opened-config-path");
            var ambient = Assert.ThrowsExactly<InvalidOperationException>(() => EnvironmentLoader.Load(development, role));
            // Path.GetFullPath would reject this value. The retired API must not
            // reach any normalization, file read, role parsing or fallback.
            var explicitPath = Assert.ThrowsExactly<InvalidOperationException>(() => EnvironmentLoader.LoadFromFile("\0", development, role));
            Assert.AreEqual("Legacy JSON/environment configuration is retired; await PortableEnvironmentLoader at the fixed installation root.",
                ambient.Message, StringComparer.Ordinal);
            Assert.AreEqual(ambient.Message, explicitPath.Message, StringComparer.Ordinal);
            Assert.IsNull(ambient.InnerException);
            Assert.IsNull(explicitPath.InnerException);
            Assert.DoesNotContain("synthetic-never-opened-config-path", ambient.Message, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(EnvironmentLoader.ConfigPathVariable, prior); }
    }
}
