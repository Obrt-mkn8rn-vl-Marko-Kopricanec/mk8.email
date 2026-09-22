using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;

namespace mk8.email.Jmap;

public static class JmapServiceExtensions
{
    public static IServiceCollection AddJmapApplication(this IServiceCollection services)
    {
        services.TryAddScoped<ISenderAuthorizationService, SenderAuthorizationService>();
        services.TryAddScoped<IEmailService, EmailService>();
        services.AddScoped<JmapAccountService>();
        services.AddScoped<JmapSessionService>();
        services.AddScoped<JmapStateService>();
        services.AddScoped<JmapStateChangeService>();
        services.AddScoped<JmapMailboxStore>();
        services.TryAddScoped<LargeObjectTransactionEffects>();
        services.TryAddScoped<MailQueueContentService>();
        services.TryAddScoped<MailboxMessageContentService>();
        services.TryAddScoped<DavResourceContentService>();
        services.AddScoped<JmapBlobService>();
        services.AddScoped<JmapBlobLargeObjectMigrationService>();
        services.AddScoped<JmapEmailBuilder>();
        services.AddScoped<JmapEmailStore>();
        services.AddScoped<JmapContactStore>();
        services.AddScoped<IJmapApplicationService, JmapApplicationService>();
        services.AddScoped<JmapIdentityService>();
        services.AddScoped<JmapVacationResponseService>();
        services.AddSingleton<JmapPushDeliveryService>();
        services.AddSingleton<JmapConcurrencyLimiter>();
        services.AddHostedService<JmapPushWorker>();
        services.AddScoped<EmailSetMethod>();
        services.AddScoped<JmapRequestProcessor>();
        services.AddScoped<IJmapMethod, CoreEchoMethod>();
        services.AddScoped<IJmapMethod, MailboxGetMethod>();
        services.AddScoped<IJmapMethod, MailboxChangesMethod>();
        services.AddScoped<IJmapMethod, MailboxQueryMethod>();
        services.AddScoped<IJmapMethod, MailboxQueryChangesMethod>();
        services.AddScoped<IJmapMethod, MailboxSetMethod>();
        services.AddScoped<IJmapMethod, ThreadGetMethod>();
        services.AddScoped<IJmapMethod, ThreadChangesMethod>();
        services.AddScoped<IJmapMethod, EmailGetMethod>();
        services.AddScoped<IJmapMethod, EmailChangesMethod>();
        services.AddScoped<IJmapMethod, EmailQueryMethod>();
        services.AddScoped<IJmapMethod, EmailQueryChangesMethod>();
        services.AddScoped<IJmapMethod>(provider => provider.GetRequiredService<EmailSetMethod>());
        services.AddScoped<IJmapMethod, EmailImportMethod>();
        services.AddScoped<IJmapMethod, EmailParseMethod>();
        services.AddScoped<IJmapMethod, EmailCopyMethod>();
        services.AddScoped<IJmapMethod, SearchSnippetGetMethod>();
        services.AddScoped<IJmapMethod, IdentityGetMethod>();
        services.AddScoped<IJmapMethod, IdentityChangesMethod>();
        services.AddScoped<IJmapMethod, IdentitySetMethod>();
        services.AddScoped<IJmapMethod, EmailSubmissionGetMethod>();
        services.AddScoped<IJmapMethod, EmailSubmissionChangesMethod>();
        services.AddScoped<IJmapMethod, EmailSubmissionQueryMethod>();
        services.AddScoped<IJmapMethod, EmailSubmissionQueryChangesMethod>();
        services.AddScoped<IJmapMethod, EmailSubmissionSetMethod>();
        services.AddScoped<IJmapMethod, VacationResponseGetMethod>();
        services.AddScoped<IJmapMethod, VacationResponseSetMethod>();
        services.AddScoped<IJmapMethod, PushSubscriptionGetMethod>();
        services.AddScoped<IJmapMethod, PushSubscriptionSetMethod>();
        services.AddScoped<IJmapMethod, BlobCopyMethod>();
        services.AddScoped<IJmapMethod, AddressBookGetMethod>();
        services.AddScoped<IJmapMethod, AddressBookChangesMethod>();
        services.AddScoped<IJmapMethod, AddressBookSetMethod>();
        services.AddScoped<IJmapMethod, ContactCardGetMethod>();
        services.AddScoped<IJmapMethod, ContactCardChangesMethod>();
        services.AddScoped<IJmapMethod, ContactCardQueryMethod>();
        services.AddScoped<IJmapMethod, ContactCardQueryChangesMethod>();
        services.AddScoped<IJmapMethod, ContactCardSetMethod>();
        services.AddScoped<IJmapMethod, ContactCardCopyMethod>();
        return services;
    }
}
