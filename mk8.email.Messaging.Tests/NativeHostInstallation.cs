using System.Globalization;
using System.Text.Json;
using mk8.email.Configuration;
using Supprocom.Secrets;

namespace mk8.email.Messaging.Tests;

internal static class NativeHostInstallation
{
    internal static async Task<(string Worker, string Gateway)> PrepareRolesAsync(string directory,
        EnvironmentConfig configuration, CancellationToken cancellationToken)
    {
        var worker = await PrepareAsync("MK8_EMAIL_TEST_WORKER_DLL", directory, configuration,
            EnvironmentValidationRole.ApplicationWorker, cancellationToken).ConfigureAwait(false);
        var gateway = await PrepareAsync("MK8_EMAIL_TEST_GATEWAY_DLL", directory, configuration,
            EnvironmentValidationRole.Gateway, cancellationToken).ConfigureAwait(false);
        return (worker, gateway);
    }

    internal static async Task<string> PrepareAsync(string assemblyVariable, string directory,
        EnvironmentConfig configuration, EnvironmentValidationRole role, CancellationToken cancellationToken)
    {
        var source = Environment.GetEnvironmentVariable(assemblyVariable);
        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source)
            || !File.Exists(source) || !File.Exists(Path.ChangeExtension(source, ".deps.json"))
            || !File.Exists(Path.ChangeExtension(source, ".runtimeconfig.json")))
        {
            throw new AssertInconclusiveException("Explicit compiled role DLL, dependencies and runtime configuration are required.");
        }
        var root = Path.Combine(directory, role.ToString());
        Directory.CreateDirectory(root);
        var sourceDirectory = Path.GetDirectoryName(source) ?? throw new InvalidDataException("The compiled input has no parent directory.");
        await CopyRoleFilesAsync(sourceDirectory, root, cancellationToken).ConfigureAwait(false);
        var settings = new List<SupprocomSecretSetting>();
        Flatten(JsonSerializer.SerializeToElement(configuration), string.Empty, settings, role);
        var options = new SupprocomSecretsOptions { EnvironmentName = "Production", FileOverridesProcessEnvironment = true };
        options.File.Directory = Path.Combine(root, "Environment");
        options.File.Import = SecretFileImport.None;
        options.File.Recovery = SecretFileRecovery.None;
        options.File.Protection = SecretFileProtection.InstallationBoundAesGcm;
        options.File.TemplateName = options.File.ActiveName;
        await new SupprocomSecretFileStore(options).ReplaceDocumentAsync(
            SupprocomSecretDocument.Serialize(settings), cancellationToken).ConfigureAwait(false);
        return Path.Combine(root, Path.GetFileName(source));
    }

    private static async Task CopyRoleFilesAsync(string source, string destination, CancellationToken token)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Linked compiled inputs are not admitted by the owned fixture.");
            var name = Path.GetFileName(path);
            // Never import another installation's active values/key or obsolete
            // appsettings. These copies are task-owned DLL INPUT points only.
            if (name.Equals("Environment", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var target = Path.Combine(destination, name);
            if (Directory.Exists(path))
            {
                Directory.CreateDirectory(target);
                await CopyRoleFilesAsync(path, target, token).ConfigureAwait(false);
            }
            else
            {
                var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                await using var inputLifetime = input.ConfigureAwait(false);
                var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await using var outputLifetime = output.ConfigureAwait(false);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
        }
    }

    private static void Flatten(JsonElement value, string prefix, List<SupprocomSecretSetting> settings, EnvironmentValidationRole role)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                Flatten(property.Value, prefix.Length == 0 ? property.Name : prefix + ":" + property.Name, settings, role);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                Flatten(item, prefix + ":" + (index++).ToString(CultureInfo.InvariantCulture), settings, role);
        }
        else if (value.ValueKind != JsonValueKind.Null)
        {
            if (role == EnvironmentValidationRole.Gateway && prefix is "OAuth:SigningKey" or "Mfa:EncryptionKey" or "Dkim:PrivateKeyPem") return;
            if (role == EnvironmentValidationRole.ApplicationWorker && prefix.StartsWith("Tls:", StringComparison.Ordinal)) return;
            if (role == EnvironmentValidationRole.ApplicationWorker && prefix.StartsWith("Admin:DataProtectionKeys:", StringComparison.Ordinal)) return;
            var scalar = value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? throw new InvalidDataException("A string setting unexpectedly contained null.")
                : value.GetRawText();
            settings.Add(new SupprocomSecretSetting(prefix, scalar));
        }
    }
}
