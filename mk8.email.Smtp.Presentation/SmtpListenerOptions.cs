using mk8.email.Configuration;

namespace mk8.email.Smtp.Presentation;

internal sealed record SmtpListenerOptions(
    string SmtpHostname,
    int SmtpPort,
    int SmtpSubmissionPort,
    int SmtpImplicitTlsPort,
    bool EnableSmtp,
    bool EnableSubmission,
    bool EnableImplicitTls,
    bool EnableStartTls,
    bool RequireTls,
    bool RequireAuth,
    bool AllowRelay,
    string? TlsCertificatePath,
    string? TlsCertificateKeyPath,
    int MaxMessageSizeBytes,
    int MaxRecipientsPerMessage,
    int ConnectionTimeoutSeconds,
    int MaxConnectionsPerIp)
{
    public static SmtpListenerOptions FromEnvironment(EnvironmentConfig environment) => new(
        environment.Smtp.Hostname,
        environment.Smtp.Port,
        environment.Smtp.SubmissionPort,
        environment.Smtp.ImplicitTlsPort,
        environment.Smtp.EnableSmtp,
        environment.Smtp.EnableSubmission,
        environment.Smtp.EnableImplicitTls,
        environment.Smtp.EnableStartTls,
        environment.Smtp.RequireTls,
        environment.Smtp.RequireAuth,
        environment.Smtp.AllowRelay,
        environment.Tls.CertificatePath,
        environment.Tls.CertificateKeyPath,
        environment.Limits.MaxMessageSizeBytes,
        environment.Limits.MaxRecipientsPerMessage,
        environment.Limits.ConnectionTimeoutSeconds,
        environment.Limits.MaxConnectionsPerIp);
}
