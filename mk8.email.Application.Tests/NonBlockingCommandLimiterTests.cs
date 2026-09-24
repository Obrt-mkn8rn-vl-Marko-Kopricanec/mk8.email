using mk8.email.Imap.Presentation;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class NonBlockingCommandLimiterTests
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
