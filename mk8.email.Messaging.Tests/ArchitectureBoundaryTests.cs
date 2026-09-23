using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Mail;
using mk8.email.MailWire;
using mk8.email.Smtp.Presentation;
using mk8.email.Contracts.Sieve;
using mk8.email.Gateway.Protocols.Sieve;

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
    public void OutboundMailContractsDoNotLoadApplicationLogic()
    {
        Assert.AreEqual("mk8.email.Contracts", typeof(IOutboundMailRelay).Assembly.GetName().Name);
        Assert.AreEqual("mk8.email.Contracts", typeof(IMailExchangeResolver).Assembly.GetName().Name);
        Assert.AreEqual("mk8.email.Contracts", typeof(IMailSubmissionQueue).Assembly.GetName().Name);
        Assert.AreEqual("mk8.email.Contracts", typeof(SmtpRelayPresentationRequest).Assembly.GetName().Name);
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
    public void MailWireHelpersAreIndependentOfApplicationAndPersistence()
    {
        var assembly = typeof(SmtpAddress).Assembly;
        Assert.AreEqual("mk8.email.MailWire", assembly.GetName().Name);
        Assert.AreSame(assembly, typeof(ManageSieveWireReader).Assembly);
        Assert.AreSame(assembly, typeof(SieveWireCapabilities).Assembly);
        Assert.AreSame(assembly, typeof(Pop3WireCodec).Assembly);
        Assert.IsFalse(assembly.GetReferencedAssemblies().Any(reference =>
            reference.Name is not null
            && (reference.Name.StartsWith("mk8.email.Application", StringComparison.Ordinal)
                || reference.Name.StartsWith("mk8.email.Infrastructure", StringComparison.Ordinal)
                || reference.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || reference.Name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))));
    }

    [TestMethod]
    public void SmtpNetworkAndListenerCodeIsOutsideApplicationCoreAndWorker()
    {
        var presentationAssembly = typeof(OutboundSmtpRelay).Assembly;
        Assert.AreEqual("mk8.email.Smtp.Presentation", presentationAssembly.GetName().Name);
        Assert.AreSame(presentationAssembly, typeof(SmtpServerService).Assembly);
        Assert.AreEqual("mk8.email.Contracts", typeof(ISmtpApplicationService).Assembly.GetName().Name);
        Assert.IsFalse(presentationAssembly.GetReferencedAssemblies().Any(reference =>
            reference.Name is not null
            && (reference.Name.StartsWith("mk8.email.Application", StringComparison.Ordinal)
                || reference.Name.StartsWith("mk8.email.Infrastructure", StringComparison.Ordinal)
                || reference.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))));
        Assert.IsFalse(typeof(mk8.email.Application.Services.MailQueueWorker).Assembly
            .GetReferencedAssemblies().Any(reference =>
                reference.Name == "mk8.email.Smtp.Presentation"));
        Assert.IsFalse(typeof(mk8.email.Application.Worker.OutboundSmtpPresentationClient).Assembly
            .GetReferencedAssemblies().Any(reference =>
                reference.Name == "mk8.email.Smtp.Presentation"));
    }

    [TestMethod]
    public void ApplicationWorkerHasNoPresentationFrameworkOrGatewayDependency()
    {
        var references = typeof(mk8.email.Application.Worker.ApplicationRequestWorker).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Gateway", StringComparison.Ordinal)
            || string.Equals(reference, "mk8.email.Dav", StringComparison.Ordinal)
                || reference.StartsWith("mk8.email.OAuth", StringComparison.Ordinal)));
        CollectionAssert.Contains(references, "mk8.email.Dav.Application");
    }

    [TestMethod]
    public void DavApplicationLogicHasNoAspNetOrPresentationDependency()
    {
        var references = typeof(mk8.email.Dav.DavServiceExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.AreEqual(
            "mk8.email.Dav.Application",
            typeof(mk8.email.Dav.DavServiceExtensions).Assembly.GetName().Name);
        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || string.Equals(reference, "mk8.email.Dav", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Gateway", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void JmapApplicationLogicHasNoAspNetDependency()
    {
        var references = typeof(mk8.email.Jmap.JmapRequestProcessor).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Gateway", StringComparison.Ordinal)));
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

    [TestMethod]
    public void GatewayOwnsManageSievePresentationWithoutApplicationLogic()
    {
        Assert.AreEqual("mk8.email.Gateway", typeof(ManageSieveServerService).Assembly.GetName().Name);
        Assert.AreEqual("mk8.email.Contracts", typeof(ISieveApplicationService).Assembly.GetName().Name);
        Assert.IsFalse(typeof(ManageSieveServerService).Assembly.GetReferencedAssemblies().Any(reference =>
            reference.Name is not null
            && (reference.Name.StartsWith("mk8.email.Application", StringComparison.Ordinal)
                || reference.Name.StartsWith("mk8.email.Infrastructure", StringComparison.Ordinal))));
    }

    [TestMethod]
    public void GatewayOwnsJmapPresentationWithoutDependingOnJmapApplicationLogic()
    {
        var endpointAssembly = typeof(
            mk8.email.Gateway.Protocols.Jmap.JmapEndpointRouteBuilderExtensions).Assembly;
        Assert.AreEqual("mk8.email.Gateway", endpointAssembly.GetName().Name);
        Assert.IsFalse(endpointAssembly.GetReferencedAssemblies().Any(reference =>
            string.Equals(reference.Name, "mk8.email.Jmap", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GatewayOwnsDavPresentationWithoutDependingOnDavApplicationLogic()
    {
        var endpointAssembly = typeof(
            mk8.email.Gateway.Protocols.Dav.GatewayDavEndpointRouteBuilderExtensions).Assembly;
        Assert.AreEqual("mk8.email.Gateway", endpointAssembly.GetName().Name);
        Assert.IsFalse(endpointAssembly.GetReferencedAssemblies().Any(reference =>
            string.Equals(reference.Name, "mk8.email.Dav.Application", StringComparison.Ordinal)
            || string.Equals(reference.Name, "mk8.email.Dav", StringComparison.Ordinal)));
    }
}
