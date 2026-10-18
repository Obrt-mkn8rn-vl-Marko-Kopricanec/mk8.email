using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using mk8.email.Configuration;

namespace mk8.email.Gateway.Security;

internal static class GatewayDataProtection
{
    internal static void AddSnapshot(IServiceCollection services, AdminConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.IsNullOrEmpty(configuration.DataProtectionKeyPath))
            throw new InvalidOperationException("Admin-cookie key directories are retired; use protected DataProtectionKeys values.");
        var repository = new SnapshotKeyRepository(configuration.DataProtectionKeys);
        services.AddDataProtection().SetApplicationName("mk8.email.admin")
            .DisableAutomaticKeyGeneration()
            .AddKeyManagementOptions(options =>
            {
                options.XmlRepository = repository;
                options.XmlEncryptor = null;
            });
    }

    internal static void RequireAvailable(IServiceProvider services)
    {
        try
        {
            var protector = services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("mk8.email.admin.startup");
            const string probe = "owned startup keyring check";
            if (!string.Equals(probe, protector.Unprotect(protector.Protect(probe)), StringComparison.Ordinal))
                throw new CryptographicException();
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The protected admin-cookie keyring is unavailable.");
        }
    }
}
