namespace mk8.email.Gateway.Protocols;

public static class GatewayProtocolPaths
{
    public static string? GetProtocol(PathString path)
    {
        if (IsOAuth(path))
            return "oauth";
        if (IsJmap(path))
            return "jmap";
        return null;
    }

    public static bool IsPublicProtocol(PathString path) =>
        IsOAuth(path) || IsJmap(path);

    public static bool IsOAuth(PathString path) =>
        path.StartsWithSegments("/oauth")
        || path.Equals("/.well-known/oauth-authorization-server")
        || path.Equals("/.well-known/openid-configuration");

    public static bool IsJmap(PathString path) =>
        path.StartsWithSegments("/jmap")
        || path.Equals("/.well-known/jmap");

    public static bool IsStreaming(PathString path) => path.Equals("/jmap/event");
}
