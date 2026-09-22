namespace mk8.email.Gateway.ApplicationBridge;

public static class GatewayApplicationServiceExtensions
{
    public static IServiceCollection AddGatewayApplicationClient(this IServiceCollection services)
    {
        services.AddSingleton(new GatewayApplicationOptions(
            $"gateway@{Environment.MachineName}",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10)));
        services.AddSingleton<IGatewayApplicationClient, GatewayApplicationClient>();
        return services;
    }
}
