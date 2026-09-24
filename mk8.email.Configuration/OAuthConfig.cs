namespace mk8.email.Configuration;

public sealed class OAuthConfig
{
    public bool EnableOAuth { get; init; }
    public bool EnableOpenIdConnect { get; init; }
    // The persisted JSON configuration encodes this URI as a string.
#pragma warning disable CA1056
    public string? PublicBaseUrl { get; init; }
#pragma warning restore CA1056
    public string ClientId { get; init; } = "thunderbird";
    public int AccessTokenMinutes { get; init; } = 10;
    public int RefreshTokenDays { get; init; } = 90;
    public int AuthorizationCodeMinutes { get; init; } = 5;
    public int IdTokenMinutes { get; init; } = 10;
    public string SigningKey { get; set; } = string.Empty;
    public string? SigningKeyFile { get; init; }

    // The existing public configuration API accepts the serialized JMAP URL string.
#pragma warning disable CA1054
    public Uri GetPublicBaseUri(string smtpHostname, string? jmapPublicBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(PublicBaseUrl))
            return new Uri(PublicBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

        var fallback = jmapPublicBaseUrl ?? $"https://{smtpHostname}";
        return new Uri(fallback.TrimEnd('/') + "/", UriKind.Absolute);
    }
#pragma warning restore CA1054
}
