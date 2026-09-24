using System.Text.Json;
using System.Text.Json.Serialization;

namespace mk8.email.Configuration;

public static class EnvironmentLoader
{
    public const string ConfigPathVariable = "MK8EMAIL_CONFIG_FILE";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static EnvironmentConfig Load(
        bool isDevelopment = false,
        EnvironmentValidationRole role = EnvironmentValidationRole.Combined)
    {
        var filePath = System.Environment.GetEnvironmentVariable(ConfigPathVariable);
        if (string.IsNullOrWhiteSpace(filePath))
            throw new InvalidOperationException($"Set {ConfigPathVariable} to the configuration file path.");

        return LoadFromFile(filePath, isDevelopment, role);
    }

    public static EnvironmentConfig LoadFromFile(
        string filePath,
        bool isDevelopment = false,
        EnvironmentValidationRole role = EnvironmentValidationRole.Combined)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("The configuration file path is required.", nameof(filePath));

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The configuration file does not exist.", fullPath);

        EnvironmentConfig config;
        try
        {
            var json = File.ReadAllText(fullPath);
            config = JsonSerializer.Deserialize<EnvironmentConfig>(json, JsonOptions)
                ?? throw new InvalidOperationException("The configuration file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The configuration file is not valid JSON: {fullPath}", ex);
        }

        ResolveSecrets(config, role);
        var errors = config.Validate(isDevelopment, role);
        if (errors.Count > 0)
        {
            var detail = string.Join(System.Environment.NewLine, errors.Select(error => $"- {error}"));
            throw new InvalidOperationException($"The mk8.email configuration is not valid:{System.Environment.NewLine}{detail}");
        }

        return config;
    }

    private static void ResolveSecrets(EnvironmentConfig config, EnvironmentValidationRole role)
    {
        config.Database.Password = ResolveSecret(
            config.Database.Password,
            config.Database.PasswordFile,
            "database password");
        if (role is not EnvironmentValidationRole.Gateway)
        {
            config.OAuth.SigningKey = ResolveSecret(
                config.OAuth.SigningKey,
                config.OAuth.SigningKeyFile,
                "OpenID Connect signing key");
            config.Mfa.EncryptionKey = ResolveSecret(
                config.Mfa.EncryptionKey,
                config.Mfa.EncryptionKeyFile,
                "MFA encryption key");
        }
        else
        {
            config.OAuth.SigningKey = string.Empty;
            config.Mfa.EncryptionKey = string.Empty;
        }
        config.Messaging.EncryptionKey = ResolveSecret(
            config.Messaging.EncryptionKey,
            config.Messaging.EncryptionKeyFile,
            "messaging encryption key");
        foreach (var key in config.Messaging.DecryptionKeys)
        {
            key.Key = ResolveSecret(
                key.Key,
                key.KeyFile,
                $"messaging decryption key {key.Id}");
        }
        config.ObjectStorage.ConnectionString = ResolveSecret(
            config.ObjectStorage.ConnectionString,
            config.ObjectStorage.ConnectionStringFile,
            "Azure Blob-compatible connection string");
    }

    private static string ResolveSecret(string directValue, string? filePath, string name)
    {
        var hasDirectValue = !string.IsNullOrEmpty(directValue);
        var hasFilePath = !string.IsNullOrWhiteSpace(filePath);

        if (hasDirectValue && hasFilePath)
            throw new InvalidOperationException($"Configure the {name} as a value or a file, but not both.");

        if (!hasFilePath)
            return directValue ?? string.Empty;

        var fullPath = Path.GetFullPath(filePath!);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The {name} file does not exist.", fullPath);

        return File.ReadAllText(fullPath).TrimEnd('\r', '\n');
    }
}
