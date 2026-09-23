using mk8.email.Gateway.Protocols.OAuth;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Gateway.ApplicationBridge;

public static class GatewayApplicationServiceExtensions
{
    public static IServiceCollection AddGatewayApplicationClient(this IServiceCollection services)
    {
        services.AddSingleton(new GatewayApplicationOptions(
            $"gateway@{Environment.MachineName}",
            TimeSpan.FromSeconds(25),
            TimeSpan.FromSeconds(10)));
        services.AddSingleton<IGatewayApplicationTransport, GatewayApplicationTransport>();
        services.AddSingleton<GatewayMailSystemStatusReader>();
        services.AddSingleton<IGatewayApplicationClient, GatewayApplicationClient>();
        services.AddSingleton<IGatewayOAuthClient, GatewayOAuthClient>();
        services.AddSingleton<IGatewayJmapClient, GatewayJmapClient>();
        return services;
    }
}
