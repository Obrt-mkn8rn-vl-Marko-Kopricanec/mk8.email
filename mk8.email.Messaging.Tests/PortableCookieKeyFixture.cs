using System.Security.Cryptography;
using mk8.email.Configuration;

namespace mk8.email.Messaging.Tests;

internal static class PortableCookieKeyFixture
{
    // Per-fixture, independently owned synthetic material; never a service key.
    internal static DataProtectionKeyConfig Create() => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
        ActivatesAt = DateTimeOffset.UtcNow.AddDays(-1),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
    };
}
