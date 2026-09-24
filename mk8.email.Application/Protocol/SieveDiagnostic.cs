namespace mk8.email.Application.Protocol;

internal sealed record SieveDiagnostic(int Line, int Column, string Message);
