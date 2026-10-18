namespace mk8.email.Configuration;

/// <summary>Source-compatible refusal surface for retired JSON/environment configuration.</summary>
public static class EnvironmentLoader
{
    // Retained for source compatibility, never consulted by any loader.
    public const string ConfigPathVariable = "MK8EMAIL_CONFIG_FILE";

    public static EnvironmentConfig Load(
        bool isDevelopment = false,
        EnvironmentValidationRole role = EnvironmentValidationRole.Combined) =>
        throw new InvalidOperationException("Legacy JSON/environment configuration is retired; await PortableEnvironmentLoader at the fixed installation root.");

    public static EnvironmentConfig LoadFromFile(
        string filePath,
        bool isDevelopment = false,
        EnvironmentValidationRole role = EnvironmentValidationRole.Combined) =>
        throw new InvalidOperationException("Legacy JSON/environment configuration is retired; await PortableEnvironmentLoader at the fixed installation root.");
}
