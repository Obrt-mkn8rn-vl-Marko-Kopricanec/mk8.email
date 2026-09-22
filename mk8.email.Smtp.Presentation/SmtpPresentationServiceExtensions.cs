using DnsClient;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Mail;

namespace mk8.email.Smtp.Presentation;

public static class SmtpPresentationServiceExtensions
{
    public static IServiceCollection AddOutboundSmtpPresentation(this IServiceCollection services)
    {
        services.AddSingleton<ILookupClient>(_ => new LookupClient(new LookupClientOptions
        {
            UseCache = true,
            Timeout = TimeSpan.FromSeconds(5),
            Retries = 2,
            ThrowDnsErrors = false,
        }));
        services.AddSingleton<IMailExchangeResolver, DnsMailExchangeResolver>();
        services.AddSingleton<OutboundSmtpRelay>();
        services.AddSingleton<IOutboundMailRelay>(provider =>
            provider.GetRequiredService<OutboundSmtpRelay>());
        services.AddSingleton<ISmtpPresentationRelay>(provider =>
            provider.GetRequiredService<OutboundSmtpRelay>());
        return services;
    }
}
