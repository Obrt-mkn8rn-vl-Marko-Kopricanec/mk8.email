namespace mk8.email.Storage;

public sealed class AzureBlobLargeObjectStoreOptions
{
    public string ContainerName { get; init; } = "mk8-email-objects";
    public string ObjectPrefix { get; init; } = string.Empty;
    public bool CreateContainerIfMissing { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ContainerName)
            || ContainerName.Length is < 3 or > 63
            || ContainerName[0] == '-'
            || ContainerName[^1] == '-'
            || ContainerName.Contains("--", StringComparison.Ordinal)
            || ContainerName.Any(character =>
                character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && character != '-'))
        {
            throw new ArgumentException(
                "An Azure Blob container name between 3 and 63 characters is required.",
                nameof(ContainerName));
        }

        if (ObjectPrefix.Length > 512
            || ObjectPrefix.StartsWith("/", StringComparison.Ordinal)
            || ObjectPrefix.Contains("//", StringComparison.Ordinal)
            || ObjectPrefix.Contains('\0'))
        {
            throw new ArgumentException("The Azure Blob object prefix is invalid.", nameof(ObjectPrefix));
        }
    }
}
