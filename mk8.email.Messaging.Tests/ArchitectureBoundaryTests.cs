using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Mail;
using mk8.email.MailWire;
using mk8.email.Smtp.Presentation;
using mk8.email.Contracts.Sieve;
using mk8.email.Contracts.Pop3;
using mk8.email.Contracts.Imap;
using mk8.email.Gateway.Protocols.Sieve;
using mk8.email.Gateway.Protocols.Pop3;
using mk8.email.Gateway.Protocols.Imap;
using mk8.email.Imap.Presentation;
using mk8.email.Wake;

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
    public void WorkerWakeProcessDoesNotLoadApplicationOrPresentationLogic()
    {
        var references = typeof(WorkerWakeProbe).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("mk8.email.Application", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Gateway", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Hosting", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Messaging", StringComparison.Ordinal)
            || reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || reference.StartsWith("Azure.Storage", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RestrictedRoleCallersEmbedIdenticalImmutableSqlPolicies()
    {
        string? expected = null;
        foreach (var assembly in new[] { typeof(GatewayDatabasePrivilegeProbe).Assembly,
                     typeof(WorkerWakeProbe).Assembly, typeof(mk8.email.Hosting.WorkerWakeSchemaTransition).Assembly })
        {
            await using var stream = assembly.GetManifestResourceStream("mk8.email.RestrictedRolePolicy.sql");
            Assert.IsNotNull(stream);
            using var reader = new StreamReader(stream);
            var policy = await reader.ReadToEndAsync();
            Assert.IsTrue(policy.TrimEnd().EndsWith("\n\\gset", StringComparison.Ordinal));
            expected ??= policy;
            Assert.AreEqual(expected, policy);
        }
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
        Assert.AreSame(assembly, typeof(ImapMailboxEncoding).Assembly);
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
    public void HostLocalHealthSnapshotBelongsToGatewayNotApplication()
    {
        Assert.AreEqual(
            "mk8.email.Gateway",
            typeof(mk8.email.Gateway.ApplicationBridge.GatewayMailSystemStatusReader)
                .Assembly.GetName().Name);
        Assert.IsFalse(typeof(mk8.email.Application.Services.ApplicationRequestDispatcher)
            .Assembly.GetTypes().Any(type => type.GetConstructors().Any(constructor =>
                constructor.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(mk8.email.Configuration.AdminConfig)))));
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
    public void GatewayOwnsPop3PresentationWithoutApplicationLogic()
    {
        Assert.AreEqual("mk8.email.Gateway", typeof(Pop3ServerService).Assembly.GetName().Name);
        Assert.AreEqual("mk8.email.Contracts", typeof(IPop3ApplicationService).Assembly.GetName().Name);
        Assert.IsFalse(typeof(Pop3ServerService).Assembly.GetReferencedAssemblies().Any(reference =>
            reference.Name is not null
            && (reference.Name.StartsWith("mk8.email.Application", StringComparison.Ordinal)
                || reference.Name.StartsWith("mk8.email.Infrastructure", StringComparison.Ordinal))));
    }

    [TestMethod]
    public void ImapApplicationContractIsTransportNeutral()
    {
        Assert.AreEqual("mk8.email.Contracts", typeof(IImapApplicationService).Assembly.GetName().Name);
        Assert.AreEqual("mk8.email.Gateway", typeof(GatewayImapApplicationService).Assembly.GetName().Name);
    }

    [TestMethod]
    public void ImapListenerLivesInPresentationAssemblyWithoutApplicationOrPersistenceDependencies()
    {
        var presentation = typeof(ImapServerService).Assembly;
        Assert.AreEqual("mk8.email.Imap.Presentation", presentation.GetName().Name);
        var references = presentation.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.IsFalse(references.Any(reference =>
            reference.StartsWith("mk8.email.Application", StringComparison.Ordinal)
            || reference.StartsWith("mk8.email.Infrastructure", StringComparison.Ordinal)
            || reference.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)));
        Assert.IsFalse(typeof(mk8.email.Application.Services.MailQueueWorker).Assembly
            .GetReferencedAssemblies().Any(reference =>
                reference.Name == "mk8.email.Imap.Presentation"));
        Assert.IsFalse(typeof(mk8.email.Application.Worker.ApplicationRequestWorker).Assembly
            .GetReferencedAssemblies().Any(reference =>
                reference.Name == "mk8.email.Imap.Presentation"));
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
    public void JmapBatchBoundaryHasNoRawApiDocumentOrHttpStatus()
    {
        Assert.IsNull(typeof(mk8.email.Contracts.Messaging.JmapBatchApplicationRequest).GetProperty("Document"));
        Assert.AreEqual(
            typeof(mk8.email.Contracts.Messaging.JmapApplicationBatch),
            typeof(mk8.email.Contracts.Messaging.JmapBatchApplicationRequest).GetProperty("Batch")?.PropertyType);
        Assert.IsNull(typeof(mk8.email.Jmap.MailApplicationException).GetProperty("StatusCode"));
        Assert.IsNull(typeof(mk8.email.Jmap.MailApplicationException).GetProperty("Type"));
        Assert.IsNull(typeof(MailApplicationFailure).GetProperty("Type"));
        Assert.IsNull(typeof(MailApplicationFailure).GetProperty("Title"));
        Assert.IsNull(typeof(JmapApplicationResult).GetProperty("Problem"));
        Assert.AreEqual(typeof(MailApplicationFailure), typeof(JmapApplicationResult).GetProperty("Failure")?.PropertyType);
        Assert.AreEqual(typeof(MailFeature[]), typeof(JmapApplicationBatch).GetProperty("Features")?.PropertyType);
        Assert.AreEqual(typeof(MailFeature[]), typeof(JmapBatchPreflight).GetProperty("Features")?.PropertyType);
        Assert.IsNull(typeof(JmapApplicationBatch).GetProperty("Capabilities"));
        Assert.IsNull(typeof(mk8.email.Jmap.JmapInvocationContext).GetProperty("Capabilities"));
        Assert.IsNull(typeof(mk8.email.Jmap.IJmapMethod).GetProperty("Capability"));
        Assert.IsNull(typeof(mk8.email.Jmap.JmapJson).GetMethod("ParseRequest"));
        Assert.AreEqual("mk8.email.Gateway", typeof(
            mk8.email.Gateway.Protocols.Jmap.GatewayJmapJson).Assembly.GetName().Name);
        Assert.AreEqual("mk8.email.Gateway", typeof(
            mk8.email.Gateway.Protocols.Jmap.GatewayJmapBatchCodec).Assembly.GetName().Name);
    }

    [TestMethod]
    public void ProfileChangesAndPushAreTypedUntilGatewayPresentation()
    {
        Assert.AreEqual(typeof(mk8.email.Contracts.Messaging.JmapApplicationProfile),
            typeof(mk8.email.Contracts.Messaging.JmapApplicationResult).GetProperty("Profile")?.PropertyType);
        Assert.AreEqual(typeof(mk8.email.Contracts.Messaging.JmapApplicationChanges),
            typeof(mk8.email.Contracts.Messaging.JmapApplicationResult).GetProperty("Changes")?.PropertyType);
        Assert.IsNull(typeof(mk8.email.Contracts.Messaging.JmapApplicationBatchResult).GetProperty("Revision"));
        Assert.AreEqual("mk8.email.Gateway", typeof(
            mk8.email.Gateway.Protocols.Jmap.GatewayJmapProfileCodec).Assembly.GetName().Name);
        Assert.AreEqual("mk8.email.Gateway", typeof(
            mk8.email.Gateway.Protocols.Jmap.GatewayJmapChangesCodec).Assembly.GetName().Name);
        Assert.IsFalse(typeof(mk8.email.Jmap.JmapAccountProfileService).Assembly.GetTypes()
            .Any(type => type.Name is "JmapSessionDocument" or "JmapPushPresentationPayload"));
    }

    [TestMethod]
    public void ArgumentBindingSyntaxAndResolutionAreOwnedByGateway()
    {
        Assert.AreEqual(typeof(JmapApplicationCall[]), typeof(JmapApplicationBatch).GetProperty("Invocations")?.PropertyType);
        Assert.AreEqual(typeof(ApplicationArgumentBinding[]), typeof(JmapApplicationCall).GetProperty("Bindings")?.PropertyType);
        Assert.AreEqual(typeof(MailOperationKind), typeof(JmapApplicationCall).GetProperty("Operation")?.PropertyType);
        Assert.AreEqual(typeof(MailOperationKind), typeof(JmapApplicationInvocation).GetProperty("Operation")?.PropertyType);
        Assert.AreEqual(typeof(MailOperationKind), typeof(ApplicationArgumentBinding).GetProperty("SourceOperation")?.PropertyType);
        Assert.IsNull(typeof(JmapApplicationCall).GetProperty("Name"));
        Assert.IsNull(typeof(JmapApplicationInvocation).GetProperty("Name"));
        Assert.IsNull(typeof(ApplicationArgumentBinding).GetProperty("SourceName"));
        Assert.IsNull(typeof(mk8.email.Jmap.IJmapMethod).GetProperty("Name"));
        Assert.AreEqual(typeof(ApplicationValuePathSegment[]), typeof(ApplicationArgumentBinding).GetProperty("Path")?.PropertyType);
        Assert.IsNull(typeof(ApplicationValuePathSegment).GetProperty("Pointer"));
        Assert.AreEqual("mk8.email.Gateway", typeof(
            mk8.email.Gateway.Protocols.Jmap.GatewayJmapArgumentBindingCodec).Assembly.GetName().Name);
        Assert.IsFalse(typeof(mk8.email.Jmap.JmapRequestProcessor)
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Any(method => method.Name is "TryResolveResultReferences" or "TryApplyJsonPointer" or "TryDecodePointerToken"));
    }

    [TestMethod]
    public void WorkerReceivesOnlyResolvedMailCommandsAndReturnsRawValueTrees()
    {
        var worker = typeof(mk8.email.Jmap.JmapRequestProcessor);
        Assert.IsFalse(worker.GetMethods().Any(method => method.Name == "ProcessAsync"));
        Assert.IsFalse(worker.Assembly.GetTypes().Any(type => type.Name is "CoreEchoMethod" or "ApplicationArgumentBindingResolver"));
        Assert.IsFalse(typeof(mk8.email.Jmap.JmapJson).GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Any(method => method.Name == "SanitizeResponse"));
        Assert.IsFalse(typeof(MailOperationCommand).GetProperties().Any(property =>
            property.Name is "Invocations" or "Bindings" or "CorrelationId" or "Document"));
        Assert.AreEqual(typeof(ApplicationValue), typeof(MailOperationResponse).GetProperty("Data")?.PropertyType);
        Assert.AreEqual("mk8.email.Gateway", typeof(mk8.email.Gateway.Protocols.Jmap.GatewayJmapBatchExecutor).Assembly.GetName().Name);
        Assert.AreEqual("mk8.email.Gateway", typeof(mk8.email.Gateway.Protocols.Jmap.GatewayJmapArgumentBindingResolver).Assembly.GetName().Name);
        Assert.AreEqual("mk8.email.Gateway", typeof(mk8.email.Gateway.ApplicationBridge.GatewayApplicationDeadline).Assembly.GetName().Name);
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
