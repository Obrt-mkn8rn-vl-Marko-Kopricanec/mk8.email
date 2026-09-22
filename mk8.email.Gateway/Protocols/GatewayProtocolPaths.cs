namespace mk8.email.Gateway.Protocols;

public static class GatewayProtocolPaths
{
    public static bool IsOAuth(PathString path) =>
        path.StartsWithSegments("/oauth")
        || path.Equals("/.well-known/oauth-authorization-server")
        || path.Equals("/.well-known/openid-configuration");
}
