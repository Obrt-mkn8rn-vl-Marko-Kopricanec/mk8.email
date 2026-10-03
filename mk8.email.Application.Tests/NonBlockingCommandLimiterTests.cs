using mk8.email.Imap.Presentation;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class NonBlockingCommandLimiterTests
{
    [TestMethod]
    public void SlotsAreNonblockingAndALeaseReleasesOnlyOnce()
    {
        var limiter = new NonBlockingCommandLimiter(2);
        var first = limiter.TryAcquire();
        Assert.IsNotNull(first);
        using var second = limiter.TryAcquire();
        Assert.IsNotNull(second);
        Assert.IsNull(limiter.TryAcquire());

        first.Dispose();
        first.Dispose();
        using var third = limiter.TryAcquire();
        Assert.IsNotNull(third);
        Assert.IsNull(limiter.TryAcquire());
    }

    [TestMethod]
    public void CapacityMustBePositive()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NonBlockingCommandLimiter(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NonBlockingCommandLimiter(-1));
    }
}
