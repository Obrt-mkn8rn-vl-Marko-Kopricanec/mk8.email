namespace mk8.email.Gateway.Protocols;

public static class GatewayProtocolPaths
{
    public static string? GetProtocol(PathString path)
    {
        if (IsOAuth(path))
            return "oauth";
        if (IsJmap(path))
            return "jmap";
        if (IsDav(path))
            return "dav";
        return null;
    }

    public static bool IsPublicProtocol(PathString path) =>
        IsOAuth(path) || IsJmap(path) || IsDav(path);

    public static bool IsOAuth(PathString path) =>
        path.StartsWithSegments("/oauth", StringComparison.Ordinal)
        || path.Equals("/.well-known/oauth-authorization-server", StringComparison.Ordinal)
        || path.Equals("/.well-known/openid-configuration", StringComparison.Ordinal);

    public static bool IsJmap(PathString path) =>
        path.StartsWithSegments("/jmap", StringComparison.Ordinal)
        || path.Equals("/.well-known/jmap", StringComparison.Ordinal);

    public static bool IsDav(PathString path) =>
        path.StartsWithSegments("/dav", StringComparison.Ordinal)
        || path.Equals("/.well-known/caldav", StringComparison.Ordinal)
        || path.Equals("/.well-known/carddav", StringComparison.Ordinal);

    public static bool IsStreaming(PathString path) => path.Equals("/jmap/event", StringComparison.Ordinal);
}
