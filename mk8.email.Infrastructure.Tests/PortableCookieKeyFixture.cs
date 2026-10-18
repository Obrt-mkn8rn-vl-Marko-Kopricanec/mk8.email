using System.Globalization;
using System.Security.Cryptography;
using mk8.email.Configuration;

namespace mk8.email.Infrastructure.Tests;

internal static class PortableCookieKeyFixture
{
    // Ephemeral finite test material, not a deployment key or approved identity.
    internal static DataProtectionKeyConfig Create() => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
        ActivatesAt = DateTimeOffset.UtcNow.AddDays(-1),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
    };

    internal static void AddValues(Dictionary<string, string> values, DataProtectionKeyConfig key)
    {
        const string prefix = "Admin:DataProtectionKeys:0:";
        values[prefix + "Id"] = key.Id.ToString("D");
        values[prefix + "CreatedAt"] = key.CreatedAt.ToString("O", CultureInfo.InvariantCulture);
        values[prefix + "ActivatesAt"] = key.ActivatesAt.ToString("O", CultureInfo.InvariantCulture);
        values[prefix + "ExpiresAt"] = key.ExpiresAt.ToString("O", CultureInfo.InvariantCulture);
        values[prefix + "MasterKey"] = key.MasterKey;
    }
}
