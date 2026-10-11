namespace mk8.email.Application.Services;

internal enum MailAuthenticationStage
{
    CandidateQuery,
    AccountPasswordVerification,
    ApplicationPasswordQuery,
    ApplicationPasswordVerification,
    ApplicationPasswordUsageUpdate,
}
