using System.Net;
using mk8.email.MailWire;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ConnectionLimiterTests
{
    [TestMethod]
    public void FailedPerAddressAdmissionRestoresGlobalCapacityAndLeasesReleaseOnce()
    {
        var limiter = new ConnectionLimiter(2);
        var firstAddress = IPAddress.Parse("192.0.2.1");
        var secondAddress = IPAddress.Parse("192.0.2.2");
        var thirdAddress = IPAddress.Parse("192.0.2.3");
        var fourthAddress = IPAddress.Parse("192.0.2.4");

        using var first = limiter.TryAcquire(firstAddress, 1);
        Assert.IsNotNull(first);
        Assert.IsNull(limiter.TryAcquire(firstAddress, 1));
        using var second = limiter.TryAcquire(secondAddress, 1);
        Assert.IsNotNull(second);
        Assert.IsNull(limiter.TryAcquire(thirdAddress, 1));

        first.Dispose();
        first.Dispose();
        using var third = limiter.TryAcquire(thirdAddress, 1);
        Assert.IsNotNull(third);
        Assert.IsNull(limiter.TryAcquire(fourthAddress, 1));
    }

    [TestMethod]
    public void ZeroCapacityRefusesConnectionsAndNegativeCapacityIsInvalid()
    {
        var limiter = new ConnectionLimiter(0);
        Assert.IsNull(limiter.TryAcquire(IPAddress.Loopback, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ConnectionLimiter(-1));
    }
}
