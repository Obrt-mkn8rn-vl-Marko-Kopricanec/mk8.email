using mk8.email.Gateway.Protocols.OAuth;

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
        services.AddSingleton<IGatewayApplicationClient, GatewayApplicationClient>();
        services.AddSingleton<IGatewayOAuthClient, GatewayOAuthClient>();
        return services;
    }
}
