namespace mk8.email.Configuration;

public sealed class ObjectStorageConfig
{
    public string Provider { get; init; } = "azure-blob";
    public string ConnectionString { get; set; } = string.Empty;
    public string? ConnectionStringFile { get; init; }
    public string ContainerName { get; init; } = "mk8-email-objects";
    public string ObjectPrefix { get; init; } = string.Empty;
    public bool CreateContainerIfMissing { get; init; }
}
