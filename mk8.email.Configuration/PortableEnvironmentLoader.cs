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
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? afterActivePrecheck = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        var config = await LoadSnapshotAsync<EnvironmentConfig>(directory, cancellationToken, afterActivePrecheck)
            .ConfigureAwait(false);
        if (config.Management.HasValues)
            throw new InvalidOperationException("Service roles must not contain management-only configuration values.");
        ValidateEnvironment(config, role);
        return config;
    }

    public static Task<EnvironmentConfig> LoadManagementAsync(
        EnvironmentValidationRole role = EnvironmentValidationRole.ApplicationWorker,
        CancellationToken cancellationToken = default) =>
        LoadManagementFromDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "Environment"), role, cancellationToken);

    internal static async Task<EnvironmentConfig> LoadManagementFromDirectoryAsync(string directory,
        EnvironmentValidationRole role, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        var config = await LoadSnapshotAsync<EnvironmentConfig>(directory, cancellationToken).ConfigureAwait(false);
        ValidateEnvironment(config, role);
        return config;
    }

    public static Task<ManagementConfig> LoadArchiveManagementAsync(CancellationToken cancellationToken = default) =>
        LoadArchiveManagementFromDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "Environment"), cancellationToken);

    internal static async Task<ManagementConfig> LoadArchiveManagementFromDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        // An archive-only CLI installation needs no database, application-account
        // password or service credentials. It has its own fixed role collection.
        var config = await LoadSnapshotAsync<ManagementConfig>(directory, cancellationToken).ConfigureAwait(false);
        if (config.AccountPassword is not null || config.TotpCode is not null || config.PgDumpExecutablePath is not null
            || config.PgRestoreExecutablePath is not null || config.WakeDatabaseHost is not null
            || config.WakeDatabasePort != 0 || config.WakeDatabaseName is not null || config.WakeDatabaseUsername is not null)
        {
            throw new InvalidOperationException("An archive-only collection must not contain unrelated management credentials or service settings.");
        }
        return config;
    }

    private static void ValidateEnvironment(EnvironmentConfig config, EnvironmentValidationRole role)
    {
        RefuseSecretFileFallbacks(config);
        if (role is EnvironmentValidationRole.ApplicationWorker
            && (config.Tls.CertificatePkcs12 is not null || config.Tls.CertificatePassword is not null))
        {
            throw new InvalidOperationException("Worker configuration must not contain Gateway TLS material.");
        }
        if (role is EnvironmentValidationRole.ApplicationWorker && config.Admin.DataProtectionKeys.Count != 0)
            throw new InvalidOperationException("Worker configuration must not contain Gateway admin-cookie keys.");
        if (role is EnvironmentValidationRole.Gateway
            && (!string.IsNullOrEmpty(config.OAuth.SigningKey)
                || !string.IsNullOrEmpty(config.Mfa.EncryptionKey)
                || !string.IsNullOrEmpty(config.Dkim.PrivateKeyPem)))
        {
            throw new InvalidOperationException(
                "Gateway configuration must not contain Worker-only OAuth, MFA or DKIM secret values.");
        }

        var errors = config.Validate(isDevelopment: false, role);
        if (errors.Count > 0)
            throw new InvalidOperationException("The portable configuration is not valid: " + string.Join("; ", errors));
    }

    public static Task<WakeConfig> LoadWakeAsync(CancellationToken cancellationToken = default) =>
        LoadWakeFromDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "Environment"), cancellationToken);

    internal static async Task<WakeConfig> LoadWakeFromDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        var config = await LoadSnapshotAsync<WakeConfig>(directory, cancellationToken).ConfigureAwait(false);
        config.Validate();
        return config;
    }

    private static async Task<T> LoadSnapshotAsync<T>(string directory, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? afterActivePrecheck = null) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();
        var installationDirectory = Path.GetFullPath(directory);
        var options = CreateActiveOnlyOptions(installationDirectory);
        var active = new FileInfo(Path.Combine(installationDirectory, ".env"));
        if (!active.Exists || active.Length == 0)
            throw new InvalidOperationException("The installation-local active configuration is missing or empty.");
        // Only the existing friend-test entry can supply this per-call awaited hook.
        if (afterActivePrecheck is not null)
            await afterActivePrecheck(cancellationToken).ConfigureAwait(false);
        var store = new SupprocomSecretFileStore(options);
        var values = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        using var source = new ConfigurationManager();
        source.AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));
        try
        {
            return source.Get<T>(binder => binder.ErrorOnUnknownConfiguration = true)
                ?? throw new InvalidOperationException("The portable configuration is empty.");
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("The portable configuration contains unknown or invalid settings.");
        }
    }

    private static SupprocomSecretsOptions CreateActiveOnlyOptions(string installationDirectory)
    {
        var options = new SupprocomSecretsOptions
        {
            EnvironmentName = "Production",
            FileOverridesProcessEnvironment = true,
        };
        options.File.Directory = installationDirectory;
        options.File.Import = SecretFileImport.None;
        options.File.Recovery = SecretFileRecovery.None;
        options.File.Protection = SecretFileProtection.InstallationBoundAesGcm;
        // 0.1.4 has no active-only creation flag. Alias its template candidate
        // to the active document itself: a separate provisioning template can
        // never be selected, even if the active file disappears after admission.
        // Missing active yields an empty snapshot; empty active fails template
        // validation before writes. Import/recovery remain disabled.
        options.File.TemplateName = options.File.ActiveName;
        return options;
    }

    private static void RefuseSecretFileFallbacks(EnvironmentConfig config)
    {
        if (config.Database.PasswordFile is not null
            || config.OAuth.SigningKeyFile is not null
            || config.Mfa.EncryptionKeyFile is not null
            || config.Messaging.EncryptionKeyFile is not null
            || config.Messaging.DecryptionKeys.Any(key => key.KeyFile is not null)
            || config.ObjectStorage.ConnectionStringFile is not null
            || config.Tls.CertificatePath is not null
            || config.Tls.CertificateKeyPath is not null
            || config.Dkim.PrivateKeyPath is not null
            || !string.IsNullOrEmpty(config.Admin.DataProtectionKeyPath))
        {
            throw new InvalidOperationException(
                "Separate secret-file settings are not supported; resolve values through Supprocom.Secrets.");
        }
    }
}
