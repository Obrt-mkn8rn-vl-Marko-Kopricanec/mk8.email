namespace mk8.email.Application.Tests;

internal sealed record AuthenticationEvent(Guid Span, bool Primary, AuthenticationPhase Phase, long ElapsedMilliseconds);
