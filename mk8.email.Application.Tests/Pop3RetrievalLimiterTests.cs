using mk8.email.Gateway.Protocols.Pop3;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class Pop3RetrievalLimiterTests
{
    [TestMethod]
    public void FullLimiterRejectsImmediatelyAndReleasedLeaseIsReusable()
    {
        var limiter = new Pop3RetrievalLimiter(2);
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
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Pop3RetrievalLimiter(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Pop3RetrievalLimiter(-1));
    }
}
