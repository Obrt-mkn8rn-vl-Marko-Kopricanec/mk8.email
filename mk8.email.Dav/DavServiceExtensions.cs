using Microsoft.Extensions.DependencyInjection;

namespace mk8.email.Dav;

public static class DavServiceExtensions
{
    public static IServiceCollection AddDavProtocol(this IServiceCollection services)
    {
        services.AddScoped<DavStore>();
        return services;
    }
}
