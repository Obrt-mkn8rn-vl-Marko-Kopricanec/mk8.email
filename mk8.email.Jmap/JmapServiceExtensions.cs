using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

public static class JmapServiceExtensions
{
    public static IServiceCollection AddJmapApplication(this IServiceCollection services)
    {
        services.TryAddScoped<ISenderAuthorizationService, SenderAuthorizationService>();
        services.TryAddScoped<IEmailService, EmailService>();
        services.AddScoped<JmapAccountService>();
        services.AddScoped<JmapAccountProfileService>();
        services.AddScoped<JmapStateService>();
        services.AddScoped<JmapStateChangeService>();
        services.AddScoped<JmapMailboxStore>();
        services.AddScoped<IMailFolderReader, MailFolderReader>();
        services.AddScoped<IMailChangesReader, MailChangesReader>();
        services.AddScoped<IMailAddressBookReader, MailAddressBookReader>();
        services.AddScoped<IMailAddressBookMutationService, MailAddressBookMutationService>();
        services.AddScoped<IMailIdentityReader, MailIdentityReader>();
        services.AddScoped<IMailIdentityMutationService, MailIdentityMutationService>();
        services.AddScoped<IMailVacationReader, MailVacationReader>();
        services.AddScoped<IMailPushSubscriptionReader, MailPushSubscriptionReader>();
        services.AddScoped<IMailPushSubscriptionMutationService, MailPushSubscriptionMutationService>();
        services.AddScoped<IMailThreadReader, MailThreadReader>();
        services.AddScoped<IMailSubmissionReader, MailSubmissionReader>();
        services.AddScoped<IMailBlobCopyService, MailBlobCopyService>();
        services.AddScoped<IMailVacationMutator, MailVacationMutator>();
        services.AddScoped<IMailSubmissionQueryService, MailSubmissionQueryService>();
        services.AddScoped<IMailFolderQueryService, MailFolderQueryService>();
        services.AddScoped<IMailMessageQueryService, MailMessageQueryService>();
        services.AddScoped<IMailSearchSnippetService, MailSearchSnippetService>();
        services.AddScoped<IMailContactCopyService, MailContactCopyService>();
        services.AddScoped<IMailContactQueryService, MailContactQueryService>();
        services.AddScoped<IMailContactReader, MailContactReader>();
        services.TryAddScoped<LargeObjectTransactionEffects>();
        services.TryAddScoped<ApplicationOperationReceiptStore>();
        services.TryAddScoped<MailQueueContentService>();
        services.TryAddScoped<MailboxMessageContentService>();
        services.TryAddScoped<DavResourceContentService>();
        services.TryAddScoped<VacationResponseContentService>();
        services.AddScoped<JmapBlobService>();
        services.AddScoped<JmapBlobLargeObjectMigrationService>();
        services.AddScoped<JmapEmailBuilder>();
        services.AddScoped<JmapEmailStore>();
        services.AddScoped<IMailImportService, MailImportService>();
        services.AddScoped<IMailCopyService, MailCopyService>();
        services.AddScoped<JmapContactStore>();
        services.AddScoped<IJmapApplicationService, JmapApplicationService>();
        services.AddScoped<JmapIdentityService>();
        services.AddScoped<JmapVacationResponseService>();
        services.TryAddSingleton<IJmapPushPresentationClient, UnavailableJmapPushPresentationClient>();
        services.AddSingleton<JmapConcurrencyLimiter>();
        services.AddSingleton<JmapPushWorker>();
        services.AddSingleton<IJmapPushWork>(provider =>
            provider.GetRequiredService<JmapPushWorker>());
        services.AddHostedService(provider => provider.GetRequiredService<JmapPushWorker>());
        services.AddScoped<EmailSetMethod>();
        services.AddScoped<JmapRequestProcessor>(provider => new JmapRequestProcessor(
            provider.GetRequiredService<IEnumerable<IJmapMethod>>(),
            provider.GetRequiredService<JmapAccountProfileService>(),
            provider.GetRequiredService<EmailDbContext>(),
            provider.GetRequiredService<EnvironmentConfig>(),
            provider.GetRequiredService<LargeObjectTransactionEffects>(),
            provider.GetRequiredService<ILogger<JmapRequestProcessor>>(),
            provider.GetRequiredService<ApplicationOperationReceiptStore>(),
            provider.GetRequiredService<IMailFolderReader>(),
            provider.GetRequiredService<IMailChangesReader>(),
            provider.GetRequiredService<IMailAddressBookReader>(),
            provider.GetRequiredService<IMailAddressBookMutationService>(),
            provider.GetRequiredService<IMailIdentityReader>(),
            provider.GetRequiredService<IMailIdentityMutationService>(),
            provider.GetRequiredService<IMailVacationReader>(),
            provider.GetRequiredService<IMailPushSubscriptionReader>(),
            provider.GetRequiredService<IMailThreadReader>(),
            provider.GetRequiredService<IMailSubmissionReader>(),
            provider.GetRequiredService<IMailBlobCopyService>(),
            provider.GetRequiredService<IMailVacationMutator>(),
            provider.GetRequiredService<IMailSubmissionQueryService>(),
            provider.GetRequiredService<IMailFolderQueryService>(),
            provider.GetRequiredService<IMailMessageQueryService>(),
            provider.GetRequiredService<IMailSearchSnippetService>(),
            provider.GetRequiredService<IMailContactCopyService>(),
            provider.GetRequiredService<IMailContactQueryService>(),
            provider.GetRequiredService<IMailContactReader>(),
            provider.GetRequiredService<IMailImportService>(),
            provider.GetRequiredService<IMailCopyService>(),
            provider.GetRequiredService<IMailPushSubscriptionMutationService>(),
            provider.GetRequiredService<IJmapPushPresentationClient>()));
        AddJmapMethods(services);
        return services;
    }

    private static void AddJmapMethods(IServiceCollection services)
    {
        services.AddScoped<IJmapMethod, MailboxSetMethod>();
        services.AddScoped<IJmapMethod, EmailGetMethod>();
        services.AddScoped<IJmapMethod>(provider => provider.GetRequiredService<EmailSetMethod>());
        services.AddScoped<IJmapMethod, EmailParseMethod>();
        services.AddScoped<IJmapMethod, EmailSubmissionSetMethod>();
        services.AddScoped<IJmapMethod, ContactCardSetMethod>();
    }
}
