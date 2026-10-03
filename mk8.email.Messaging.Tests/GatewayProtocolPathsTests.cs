using Microsoft.AspNetCore.Http;
using mk8.email.Gateway.Protocols;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this class; all path vectors are executed in the retained suites.")]
internal sealed class GatewayProtocolPathsTests
{
    [TestMethod]
    [DataRow("/OAuth/TOKEN/", "oauth", false)]
    [DataRow("/OAUTH", "oauth", false)]
    [DataRow("/.WELL-KNOWN/OAUTH-AUTHORIZATION-SERVER/", "oauth", false)]
    [DataRow("/.Well-Known/OpenID-Configuration", "oauth", false)]
    [DataRow("/JMAP/api", "jmap", false)]
    [DataRow("/.Well-Known/JMAP/", "jmap", false)]
    [DataRow("/jmap/event", "jmap", true)]
    [DataRow("/jmap/EVENT", "jmap", true)]
    [DataRow("/jmap/event/", "jmap", true)]
    [DataRow("/JMAP/EVENT/", "jmap", true)]
    [DataRow("/DAV/", "dav", false)]
    [DataRow("/.WELL-KNOWN/CALDAV/", "dav", false)]
    [DataRow("/.Well-Known/CardDAV", "dav", false)]
    public void RouterAcceptedAliasesShareProtocolAndStreamingMode(string path, string protocol, bool streaming)
    {
        var value = new PathString(path);
        Assert.AreEqual(protocol, GatewayProtocolPaths.GetProtocol(value), StringComparer.Ordinal);
        Assert.IsTrue(GatewayProtocolPaths.IsPublicProtocol(value));
        Assert.AreEqual(streaming, GatewayProtocolPaths.IsStreaming(value));
    }

    [TestMethod]
    [DataRow("/oauth-other/token")]
    [DataRow("/jmap-other/event")]
    [DataRow("/dav-other/")]
    [DataRow("/.well-known/jmap/extra")]
    [DataRow("/.well-known/jmap//")]
    [DataRow("/.well-known/carddav-other")]
    [DataRow("/Error")]
    [DataRow("")]
    public void UnrelatedRoutesRemainAdministrative(string path)
    {
        var value = new PathString(path);
        Assert.IsNull(GatewayProtocolPaths.GetProtocol(value));
        Assert.IsFalse(GatewayProtocolPaths.IsPublicProtocol(value));
        Assert.IsFalse(GatewayProtocolPaths.IsStreaming(value));
    }

    [TestMethod]
    [DataRow("/jmap/event/extra")]
    [DataRow("/jmap/event//")]
    [DataRow("/jmap/events")]
    public void NonEventRoutesWithinNamespaceAreNotStreamed(string path) =>
        Assert.IsFalse(GatewayProtocolPaths.IsStreaming(new PathString(path)));
}
