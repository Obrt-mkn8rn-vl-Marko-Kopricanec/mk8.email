namespace mk8.email.Configuration;

public sealed class DkimConfig
{
    public string? PrivateKeyPath { get; init; }
    public string Selector { get; init; } = "default";
    public bool EnableSigning { get; init; }
}
