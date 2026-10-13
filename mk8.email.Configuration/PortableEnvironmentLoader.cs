using Microsoft.Extensions.Configuration;
using Supprocom.Secrets;

namespace mk8.email.Configuration;

/// <summary>Loads one role's portable configuration without OS-managed configuration fallback.</summary>
public static class PortableEnvironmentLoader
{
    public static Task<EnvironmentConfig> LoadAsync(
        EnvironmentValidationRole role = EnvironmentValidationRole.Combined,
        CancellationToken cancellationToken = default) =>
        LoadFromDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "Environment"), role, cancellationToken);

    // Only deterministic tests select a separate owned installation root. Services
    // expose no directory, file, environment or CLI service-value override.
    internal static async Task<EnvironmentConfig> LoadFromDirectoryAsync(
        string directory,
        EnvironmentValidationRole role,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        cancellationToken.ThrowIfCancellationRequested();

        var options = new SupprocomSecretsOptions
        {
            EnvironmentName = "Production",
            FileOverridesProcessEnvironment = true,
        };
        options.File.Directory = Path.GetFullPath(directory);
        options.File.Import = SecretFileImport.None;
        options.File.Recovery = SecretFileRecovery.None;
        options.File.Protection = SecretFileProtection.InstallationBoundAesGcm;

        var active = new FileInfo(Path.Combine(options.File.Directory, ".env"));
        if (!active.Exists || active.Length == 0)
            throw new InvalidOperationException("The installation-local active configuration is missing or empty.");

        // The builtin store refuses external pointers and never resolves provider
        // manifests/adapters. One awaited snapshot owns the startup collection.
        var store = new SupprocomSecretFileStore(options);
        var values = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        using var source = new ConfigurationManager();
        source.AddInMemoryCollection(values.Select(pair =>
            new KeyValuePair<string, string?>(pair.Key, pair.Value)));
        EnvironmentConfig config;
        try
        {
            config = source.Get<EnvironmentConfig>(binder => binder.ErrorOnUnknownConfiguration = true)
                ?? throw new InvalidOperationException("The portable configuration is empty.");
        }
        catch (InvalidOperationException)
        {
            // Binder diagnostics can include a failed conversion's raw value.
            throw new InvalidOperationException("The portable configuration contains unknown or invalid settings.");
        }

        RefuseSecretFileFallbacks(config);
        if (role is EnvironmentValidationRole.Gateway
            && (!string.IsNullOrEmpty(config.OAuth.SigningKey)
                || !string.IsNullOrEmpty(config.Mfa.EncryptionKey)))
        {
            throw new InvalidOperationException(
                "Gateway configuration must not contain Worker-only OAuth or MFA secret values.");
        }

        var errors = config.Validate(isDevelopment: false, role);
        if (errors.Count > 0)
            throw new InvalidOperationException("The portable configuration is not valid: " + string.Join("; ", errors));
        return config;
    }

    private static void RefuseSecretFileFallbacks(EnvironmentConfig config)
    {
        if (config.Database.PasswordFile is not null
            || config.OAuth.SigningKeyFile is not null
            || config.Mfa.EncryptionKeyFile is not null
            || config.Messaging.EncryptionKeyFile is not null
            || config.Messaging.DecryptionKeys.Any(key => key.KeyFile is not null)
            || config.ObjectStorage.ConnectionStringFile is not null)
        {
            throw new InvalidOperationException(
                "Separate secret-file settings are not supported; resolve values through Supprocom.Secrets.");
        }
    }
}
