using DnsClient;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;

namespace mk8.email.Application;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IInboxService, InboxService>();
        services.AddScoped<IAddressService, AddressService>();
        services.AddScoped<ICompanyService, CompanyService>();
        services.AddScoped<IMailAdministrationService, MailAdministrationService>();
        services.AddScoped<IApplicationPasswordService, ApplicationPasswordService>();
        services.AddScoped<IOAuthTokenService, OAuthTokenService>();
        services.AddScoped<IOAuthAuthorizationService, OAuthAuthorizationService>();
        services.AddScoped<IOAuthApplicationService, OAuthApplicationService>();
        services.AddSingleton<IOpenIdConnectService, OpenIdConnectService>();
        services.AddScoped<IMfaService, MfaService>();
        services.AddScoped<IMailAuthenticator, MailAuthenticator>();
        services.AddScoped<IMailSystemStatusService, MailSystemStatusService>();
        services.AddScoped<ISeederService, SeederService>();
        services.AddScoped<IDatabaseInitializationService, DatabaseInitializationService>();
        services.AddScoped<IApplicationRequestDispatcher, ApplicationRequestDispatcher>();

        return services;
    }

    public static IServiceCollection AddMailProtocolServers(this IServiceCollection services)
    {
        services.AddSingleton<ILookupClient>(_ => new LookupClient(new LookupClientOptions
        {
            UseCache = true,
            Timeout = TimeSpan.FromSeconds(5),
            Retries = 2,
            ThrowDnsErrors = false,
        }));
        services.AddSingleton<IMailExchangeResolver, DnsMailExchangeResolver>();
        services.AddSingleton<IOutboundMailRelay, OutboundSmtpRelay>();
        services.AddSingleton<IMailScanner, RspamdMailScanner>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ISenderAuthorizationService, SenderAuthorizationService>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<ISieveScriptService, SieveScriptService>();
        services.AddScoped<ISieveFilterService, SieveFilterService>();
        services.AddScoped<IVacationResponder, VacationResponder>();
        services.AddScoped<IMailSubmissionQueue, PostgresMailSubmissionQueue>();
        services.AddHostedService<MailQueueWorker>();
        services.AddHostedService<SmtpServerService>();
        services.AddHostedService<ImapServerService>();
        services.AddHostedService<Pop3ServerService>();
        services.AddHostedService<ManageSieveServerService>();

        return services;
    }
}
