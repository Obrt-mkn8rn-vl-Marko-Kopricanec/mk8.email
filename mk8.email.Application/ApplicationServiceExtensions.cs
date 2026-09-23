using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Imap;
using mk8.email.Contracts.Pop3;
using mk8.email.Contracts.Sieve;

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
        services.AddScoped<LargeObjectTransactionEffects>();
        services.AddScoped<MailQueueContentService>();
        services.AddScoped<MailQueueMaintenanceService>();
        services.AddScoped<MailQueueLargeObjectMigrationService>();
        services.AddScoped<MailboxMessageContentService>();
        services.AddScoped<MailboxMessageLargeObjectMigrationService>();
        services.AddScoped<DavResourceContentService>();
        services.AddScoped<DavResourceLargeObjectMigrationService>();
        services.AddScoped<SieveScriptContentService>();
        services.AddScoped<SieveScriptLargeObjectMigrationService>();

        return services;
    }

    public static IServiceCollection AddMailApplicationWorker(this IServiceCollection services)
    {
        services.AddSingleton<IMailScanner, RspamdMailScanner>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ISenderAuthorizationService, SenderAuthorizationService>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<ISieveScriptService, SieveScriptService>();
        services.AddScoped<ISieveApplicationService, SieveApplicationService>();
        services.AddScoped<IPop3ApplicationService, Pop3ApplicationService>();
        services.AddScoped<IImapApplicationService, ImapApplicationService>();
        services.AddScoped<ISieveFilterService, SieveFilterService>();
        services.AddScoped<IVacationResponder, VacationResponder>();
        services.AddScoped<IMailSubmissionQueue, PostgresMailSubmissionQueue>();
        services.AddScoped<ISmtpApplicationService, SmtpApplicationService>();
        services.AddHostedService<MailQueueWorker>();

        return services;
    }

}
