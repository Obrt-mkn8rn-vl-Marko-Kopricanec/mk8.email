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
        if (IsAutodiscover(path))
            return "autodiscover";
        if (IsEws(path))
            return "ews";
        return null;
    }

    public static bool IsPublicProtocol(PathString path) =>
        IsOAuth(path) || IsJmap(path) || IsDav(path) || IsAutodiscover(path) || IsEws(path);

    public static bool IsEws(PathString path) =>
        path.StartsWithSegments("/ews", StringComparison.OrdinalIgnoreCase);

    public static bool IsAutodiscover(PathString path) =>
        path.StartsWithSegments("/autodiscover", StringComparison.OrdinalIgnoreCase);

    public static bool IsOAuth(PathString path) =>
        path.StartsWithSegments("/oauth", StringComparison.OrdinalIgnoreCase)
        || MatchesLiteralRoute(path, "/.well-known/oauth-authorization-server")
        || MatchesLiteralRoute(path, "/.well-known/openid-configuration");

    public static bool IsJmap(PathString path) =>
        path.StartsWithSegments("/jmap", StringComparison.OrdinalIgnoreCase)
        || MatchesLiteralRoute(path, "/.well-known/jmap");

    public static bool IsDav(PathString path) =>
        path.StartsWithSegments("/dav", StringComparison.OrdinalIgnoreCase)
        || MatchesLiteralRoute(path, "/.well-known/caldav")
        || MatchesLiteralRoute(path, "/.well-known/carddav");

    public static bool IsStreaming(PathString path) => MatchesLiteralRoute(path, "/jmap/event");

    private static bool MatchesLiteralRoute(PathString path, string route)
    {
        // ASP.NET literal routes ignore casing and one trailing empty segment.
        // Keep pre-routing capture, error ownership and SSE mode on that same surface.
        var value = path.Value.AsSpan();
        if (value.Length > 0 && value[^1] == '/')
            value = value[..^1];
        return value.Equals(route.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }
}
