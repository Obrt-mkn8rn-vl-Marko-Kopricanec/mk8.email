using System.Text.Json;
using System.Text.Json.Serialization;

namespace mk8.email.Infrastructure.Environment;

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

    public static EnvironmentConfig Load(bool isDevelopment = false)
    {
        var filePath = System.Environment.GetEnvironmentVariable(ConfigPathVariable);
        if (string.IsNullOrWhiteSpace(filePath))
            throw new InvalidOperationException($"Set {ConfigPathVariable} to the configuration file path.");

        return LoadFromFile(filePath, isDevelopment);
    }

    public static EnvironmentConfig LoadFromFile(string filePath, bool isDevelopment = false)
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

        config.Database.Password = ResolveSecret(
            config.Database.Password,
            config.Database.PasswordFile,
            "database password");
        config.Mfa.EncryptionKey = ResolveSecret(
            config.Mfa.EncryptionKey,
            config.Mfa.EncryptionKeyFile,
            "MFA encryption key");
        var errors = config.Validate(isDevelopment);
        if (errors.Count > 0)
        {
            var detail = string.Join(System.Environment.NewLine, errors.Select(error => $"- {error}"));
            throw new InvalidOperationException($"The mk8.email configuration is not valid:{System.Environment.NewLine}{detail}");
        }

        return config;
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
