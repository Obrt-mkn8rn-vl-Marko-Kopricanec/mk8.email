namespace mk8.email.Configuration;

public sealed class FilteringConfig
{
    public string RspamdEndpoint { get; init; } = "http://127.0.0.1:11333/checkv2";
    public int TimeoutSeconds { get; init; } = 70;
}
