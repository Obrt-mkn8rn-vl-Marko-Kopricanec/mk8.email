namespace mk8.email.Storage;

public sealed class AzureBlobLargeObjectStoreOptions
{
    public string ContainerName { get; init; } = "mk8-email-objects";
    public string ObjectPrefix { get; init; } = string.Empty;
    public bool CreateContainerIfMissing { get; init; }

    internal static void Validate(AzureBlobLargeObjectStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ContainerName)
            || options.ContainerName.Length is < 3 or > 63
            || options.ContainerName[0] == '-'
            || options.ContainerName[^1] == '-'
            || options.ContainerName.Contains("--", StringComparison.Ordinal)
            || options.ContainerName.Any(character =>
                character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && character != '-'))
        {
            throw new ArgumentException(
                "An Azure Blob ContainerName between 3 and 63 characters is required.",
                nameof(options));
        }

        if (options.ObjectPrefix.Length > 512
            || options.ObjectPrefix.StartsWith('/')
            || options.ObjectPrefix.Contains("//", StringComparison.Ordinal)
            || options.ObjectPrefix.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The Azure Blob ObjectPrefix is invalid.", nameof(options));
        }
    }
}
