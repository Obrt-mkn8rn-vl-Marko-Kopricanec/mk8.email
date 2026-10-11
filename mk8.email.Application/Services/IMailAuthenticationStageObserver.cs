namespace mk8.email.Application.Services;

// Internal, opt-in diagnostics: no credential, account, query, result or error payload.
internal interface IMailAuthenticationStageObserver
{
    void Record(Guid call, MailAuthenticationStage stage, MailAuthenticationStagePhase phase);
}
