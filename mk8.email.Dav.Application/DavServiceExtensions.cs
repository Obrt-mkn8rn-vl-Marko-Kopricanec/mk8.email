using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;

namespace mk8.email.Dav;

public static class DavServiceExtensions
{
    public static IServiceCollection AddDavProtocol(this IServiceCollection services)
    {
        services.TryAddScoped<LargeObjectTransactionEffects>();
        services.AddScoped<DavResourceContentService>();
        services.AddScoped<DavResourceLargeObjectMigrationService>();
        services.AddScoped<DavStore>();
        services.AddScoped<DavSchedulingService>();
        services.AddScoped<IDavApplicationService, DavApplicationService>();
        return services;
    }
}
