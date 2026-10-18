using mk8.email.Configuration;
using Npgsql;
using Supprocom.Secrets;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "MSTest discovers this public configuration fixture by reflection.")]
public sealed class PortableWakeConfigurationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ProtectedWakeValuesRemainOneIndependentSnapshot()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var config = await PortableEnvironmentLoader.LoadWakeFromDirectoryAsync(fixture.Root.FullName, TestContext.CancellationToken).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture.Root.FullName, ".env"), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(bytes).Contains("synthetic-wake-password", StringComparison.Ordinal));
        fixture.Values["DatabasePassword"] = "later-synthetic-password";
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var connection = new NpgsqlConnectionStringBuilder(config.BuildConnectionString());
        Assert.AreEqual("database.example.test", connection.Host, StringComparer.Ordinal);
        Assert.AreEqual("synthetic-wake-password", connection.Password, StringComparer.Ordinal);
        Assert.AreEqual(SslMode.VerifyFull, connection.SslMode);
        Assert.AreEqual(fixture.Values["WorkerAssemblyPath"], config.WorkerAssemblyPath, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("ConnectionStringFile", "never-read-secret-file")]
    [DataRow("WorkerConfigPath", "never-read-worker-json")]
    [DataRow("DatabasePassword", "short")]
    [DataRow("DatabaseHost", "https://database.invalid")]
    [DataRow("DatabaseSslMode", "Disable")]
    public async Task LegacyAndInvalidWakeValuesRefuseBeforeAnyDatabaseOrChild(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.Values[key] = value;
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            PortableEnvironmentLoader.LoadWakeFromDirectoryAsync(fixture.Root.FullName, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.IsNull(error.InnerException);
        Assert.IsFalse(error.Message.Contains(value, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExplicitLoopbackCanSelectUnencryptedOwnedDatabaseTransport()
    {
        using var fixture = new Fixture();
        fixture.Values["DatabaseHost"] = "127.0.0.1";
        fixture.Values["DatabaseSslMode"] = "Disable";
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var config = await PortableEnvironmentLoader.LoadWakeFromDirectoryAsync(fixture.Root.FullName, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(SslMode.Disable, new NpgsqlConnectionStringBuilder(config.BuildConnectionString()).SslMode);
    }

    private sealed class Fixture : IDisposable
    {
        internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("mk8-wake-config-");
        internal Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal)
        {
            ["DatabaseHost"] = "database.example.test",
            ["DatabasePort"] = "5432",
            ["DatabaseName"] = "owned_fixture",
            ["DatabaseUsername"] = "owned_wake",
            ["DatabasePassword"] = "synthetic-wake-password",
        };

        internal Task WriteAsync(CancellationToken cancellationToken)
        {
            Values["DotnetHostPath"] = Path.Combine(Root.FullName, "dotnet");
            Values["WorkerAssemblyPath"] = Path.Combine(Root.FullName, "Worker", "mk8.email.Application.Worker.dll");
            var text = SupprocomSecretDocument.Serialize(Values.Select(pair => new SupprocomSecretSetting(pair.Key, pair.Value)));
            return File.WriteAllTextAsync(Path.Combine(Root.FullName, ".env"), text, cancellationToken);
        }

        public void Dispose()
        {
            Root.Delete(recursive: true);
            GC.SuppressFinalize(this);
        }
    }
}
