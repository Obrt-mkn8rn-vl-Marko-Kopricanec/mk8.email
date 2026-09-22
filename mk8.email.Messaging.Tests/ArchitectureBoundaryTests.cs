using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging.Tests;

[TestClass]
public sealed class ArchitectureBoundaryTests
{
    [TestMethod]
    public void MessagingContractsDoNotReferenceHostingOrPersistenceFrameworks()
    {
        var references = typeof(ApplicationRequest).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || reference.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || reference.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void MessagingTransportDoesNotReferenceAspNetOrApplicationLogic()
    {
        var references = typeof(PostgresApplicationBus).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Application", StringComparison.Ordinal)
                || reference.StartsWith("mk8.email.Infrastructure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void StorageAdapterOwnsAzureSdkWithoutDependingOnApplicationLogic()
    {
        var references = typeof(mk8.email.Storage.AzureBlobLargeObjectStore).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsTrue(references.Any(reference =>
            reference.StartsWith("Azure.Storage.Blobs", StringComparison.Ordinal)));
        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Application", StringComparison.Ordinal)
                || reference.StartsWith("mk8.email.Infrastructure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ApplicationWorkerHasNoPresentationFrameworkOrProtocolAssemblyDependency()
    {
        var references = typeof(mk8.email.Application.Worker.ApplicationRequestWorker).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Gateway", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Jmap", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Dav", StringComparison.Ordinal)
                || reference.StartsWith("mk8.email.OAuth", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SharedConfigurationHasNoApplicationOrEntityFrameworkDependency()
    {
        var references = typeof(mk8.email.Configuration.EnvironmentConfig).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.AreEqual(
            "mk8.email.Configuration",
            typeof(mk8.email.Configuration.EnvironmentConfig).Assembly.GetName().Name);
        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || reference.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Application", StringComparison.Ordinal)
                || reference.StartsWith("mk8.email.Infrastructure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GatewayHasNoApplicationOrEntityFrameworkInfrastructureDependency()
    {
        var references = typeof(mk8.email.Gateway.ApplicationBridge.GatewayApplicationClient).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Application", StringComparison.Ordinal)
                || reference.StartsWith("mk8.email.Infrastructure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SharedHostingCompositionHasNoPresentationOrApplicationLogicDependency()
    {
        var references = typeof(mk8.email.Hosting.DistributedMessagingServiceExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Gateway", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Application", StringComparison.Ordinal)
                || reference.StartsWith("mk8.email.Infrastructure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GatewayOwnsOAuthPresentationWithoutAnOAuthHostingAssembly()
    {
        Assert.AreEqual(
            "mk8.email.Gateway",
            typeof(mk8.email.Gateway.Protocols.OAuth.OAuthEndpointRouteBuilderExtensions)
                .Assembly
                .GetName()
                .Name);
        Assert.AreEqual(
            "mk8.email.Contracts",
            typeof(mk8.email.Contracts.Protocol.OAuthProtocolValues)
                .Assembly
                .GetName()
                .Name);
    }
}
