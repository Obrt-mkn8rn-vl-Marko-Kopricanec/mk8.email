using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Hosting;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Gateway;
using Supprocom.Secrets;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest executes these internal host-construction controls by reflection.")]
internal sealed class PortableHostBootstrapTests
{
    public TestContext? TestContext { get; set; }

    [TestMethod]
    public async Task EmptyHostConstructionCannotConsumePoisonedAmbientInputs()
    {
        Assert.IsNotNull(TestContext);
        using var poison = new AmbientPoison();
        await poison.InstallAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var worker = WorkerHostBootstrap.CreateBuilder();
        using var workerHost = worker.Build();
        AssertExplicitHost(worker.Environment, worker.Configuration);
        var gateway = GatewayHostBootstrap.CreateBuilder(Configuration());
        var app = gateway.Build();
        await using var appLifetime = app.ConfigureAwait(false);
        AssertExplicitHost(gateway.Environment, gateway.Configuration);
        Assert.IsTrue(File.Exists(Path.Combine(poison.Root.FullName, "appsettings.json")));
    }

    [TestMethod]
    [DataRow("127.0.0.1", "203.0.113.17", true)]
    [DataRow("::1", "203.0.113.17", true)]
    [DataRow("127.0.0.2", "203.0.113.17", false)]
    [DataRow("192.0.2.10", "127.0.0.1", false)]
    public async Task ForwardedValuesRequireTheExactLoopbackPeer(string peer, string forwarded, bool trusted)
    {
        var builder = GatewayHostBootstrap.CreateBuilder(Configuration());
        var app = builder.Build();
        await using var appLifetime = app.ConfigureAwait(false);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Host = new HostString("mail.example.test");
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = forwarded;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "forged.invalid";
        var pipeline = new ApplicationBuilder(app.Services).UseHostFiltering().UseForwardedHeaders();
        pipeline.Run(_ => Task.CompletedTask);
        await pipeline.Build()(context).ConfigureAwait(false);
        Assert.AreEqual(IPAddress.Parse(trusted ? forwarded : peer), context.Connection.RemoteIpAddress);
        Assert.AreEqual(trusted ? "https" : "http", context.Request.Scheme, StringComparer.Ordinal);
        Assert.AreEqual("mail.example.test", context.Request.Host.Host, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("mail.example.test", 200)]
    [DataRow("MAIL.EXAMPLE.TEST:12345", 200)]
    [DataRow("unknown.invalid", 400)]
    [DataRow("", 400)]
    public async Task OnlyConfiguredHostsReachTheNextDelegate(string host, int status)
    {
        var builder = GatewayHostBootstrap.CreateBuilder(Configuration());
        var app = builder.Build();
        await using var appLifetime = app.ConfigureAwait(false);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Host = new HostString(host);
        var response = new MemoryStream();
        await using var responseLifetime = response.ConfigureAwait(false);
        context.Response.Body = response;
        var entered = false;
        var pipeline = new ApplicationBuilder(app.Services).UseHostFiltering();
        pipeline.Run(_ => { entered = true; return Task.CompletedTask; });
        await pipeline.Build()(context).ConfigureAwait(false);
        Assert.AreEqual(status, context.Response.StatusCode);
        Assert.AreEqual(status == 200, entered);
    }

    [TestMethod]
    [DataRow("*")]
    [DataRow("*.example.test")]
    [DataRow("example.test:443")]
    [DataRow("https://example.test")]
    [DataRow("0.0.0.0")]
    [DataRow("::")]
    [DataRow("[::]")]
    public void HostWildcardsAndLocatorsAreNotAllowed(string host)
    {
        var errors = new GatewayHttpConfig { ListenAddress = "127.0.0.1", Port = 12345, AllowedHosts = [host] }.Validate();
        Assert.IsNotEmpty(errors);
    }

    [TestMethod]
    public void HttpListenerCannotConflictWithNativeOrDeclaredSharedHttpPorts()
    {
        var http = Configuration().Http;
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewayHostBootstrap.CreateBuilder(new EnvironmentConfig
        {
            Http = http,
            Smtp = new SmtpConfig { EnableSmtp = true, Port = http.Port },
            Dav = new DavConfig { EnableDav = false },
            OAuth = new OAuthConfig { EnableOAuth = false },
            Jmap = new JmapConfig { EnableJmap = false },
        }));
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewayHostBootstrap.CreateBuilder(new EnvironmentConfig
        {
            Http = http,
            Jmap = new JmapConfig { EnableJmap = true, Port = http.Port + 1 },
        }));
    }

    [TestMethod]
    public void WorkerIdentityMustNotFallBackToTheMachineName()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkerHostBootstrap.RequireWorkerIdentity(new EnvironmentConfig()));
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkerHostBootstrap.RequireWorkerIdentity(
            new EnvironmentConfig { Messaging = new MessagingConfig { Enabled = true } }));
        Assert.AreEqual("owned-worker", WorkerHostBootstrap.RequireWorkerIdentity(
            new EnvironmentConfig { Messaging = new MessagingConfig { Enabled = true, WorkerId = "owned-worker" } }), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("Gateway")]
    [DataRow("Worker")]
    [DataRow("CLI")]
    public async Task PublishedTemplateKeysBindToTheActualRoleSchema(string role)
    {
        Assert.IsNotNull(TestContext);
        var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "RoleTemplates", role + ".env.template"), TestContext.CancellationToken).ConfigureAwait(false);
        var settings = SupprocomSecretDocument.Parse(text).Settings;
        Assert.IsNotEmpty(settings);
        Assert.IsTrue(settings.All(setting => setting.Value.Length == 0), "Publish only nonsecret blank outlines.");
        // Finite reserved fixture values check binding, not template activation,
        // complete domain validity, provisioning or approved deployment values.
        using var source = new ConfigurationManager();
        source.AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(
            setting.Key, setting.Key switch
            {
                "Database:Host" => "database.example.test",
                "Database:Name" => "fixture_database",
                "Http:Port" => "12345",
                "Database:Port" or "Management:WakeDatabasePort" => "5432",
                "Messaging:Enabled" => "true",
                "Admin:DataProtectionKeys:0:Id" => "e726fa39-7db4-4f44-9247-a87450dc854f",
                "Admin:DataProtectionKeys:0:CreatedAt" or "Admin:DataProtectionKeys:0:ActivatesAt"
                    or "Admin:DataProtectionKeys:0:ExpiresAt" => "2026-01-01T00:00:00+00:00",
                _ => "fixture",
            })));
        var bound = source.Get<EnvironmentConfig>(options => options.ErrorOnUnknownConfiguration = true);
        Assert.IsNotNull(bound);
        Assert.AreEqual("database.example.test", bound.Database.Host, StringComparer.Ordinal);
        Assert.AreEqual("fixture_database", bound.Database.Name, StringComparer.Ordinal);
    }

    private static EnvironmentConfig Configuration() => new()
    {
        Http = new GatewayHttpConfig { ListenAddress = "127.0.0.1", Port = 12345, AllowedHosts = ["mail.example.test"] },
        Dav = new DavConfig { EnableDav = false },
        OAuth = new OAuthConfig { EnableOAuth = false },
        Jmap = new JmapConfig { EnableJmap = false },
    };

    private static void AssertExplicitHost(IHostEnvironment environment, ConfigurationManager configuration)
    {
        Assert.AreEqual(Environments.Production, environment.EnvironmentName, StringComparer.Ordinal);
        Assert.AreEqual(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(environment.ContentRootPath), StringComparer.Ordinal);
        Assert.IsNull(configuration["AmbientBootstrapCanary"]);
        AssertOnlyExplicitMemorySources(configuration, new HashSet<IConfigurationRoot>(ReferenceEqualityComparer.Instance));
    }

    private static void AssertOnlyExplicitMemorySources(IConfigurationRoot configuration,
        HashSet<IConfigurationRoot> visited)
    {
        Assert.IsTrue(visited.Add(configuration), "Configuration chaining must not contain a cycle.");
        foreach (var provider in configuration.Providers)
        {
            if (provider is MemoryConfigurationProvider) continue;
            // Empty web hosting chains its explicit host configuration. Inspect
            // the entire backing root rather than exempting an opaque provider.
            Assert.IsInstanceOfType<ChainedConfigurationProvider>(provider);
            var backing = ((ChainedConfigurationProvider)provider).Configuration;
            Assert.IsInstanceOfType<IConfigurationRoot>(backing);
            AssertOnlyExplicitMemorySources((IConfigurationRoot)backing, visited);
        }
    }

    private sealed class AmbientPoison : IDisposable
    {
        private static readonly string[] Names = ["DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "DOTNET_CONTENTROOT", "ASPNETCORE_CONTENTROOT", "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "AmbientBootstrapCanary"];
        private readonly string?[] _originals = [.. Names.Select(Environment.GetEnvironmentVariable)];
        private readonly string _originalDirectory = Environment.CurrentDirectory;
        internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("mk8-ambient-poison-");

        internal async Task InstallAsync(CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(Path.Combine(Root.FullName, "appsettings.json"), "not valid JSON", cancellationToken).ConfigureAwait(false);
            Environment.CurrentDirectory = Root.FullName;
            foreach (var name in Names) Environment.SetEnvironmentVariable(name, "not-an-admitted-startup-value");
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        }

        public void Dispose()
        {
            Environment.CurrentDirectory = _originalDirectory;
            for (var index = 0; index < Names.Length; index++) Environment.SetEnvironmentVariable(Names[index], _originals[index]);
            Root.Delete(recursive: true);
            GC.SuppressFinalize(this);
        }
    }
}
