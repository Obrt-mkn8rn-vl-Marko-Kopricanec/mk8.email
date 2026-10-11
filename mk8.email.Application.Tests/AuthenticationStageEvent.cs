using mk8.email.Application.Services;

namespace mk8.email.Application.Tests;

internal sealed record AuthenticationStageEvent(
    Guid Call, MailAuthenticationStage Stage, MailAuthenticationStagePhase Phase, long ElapsedMilliseconds);
