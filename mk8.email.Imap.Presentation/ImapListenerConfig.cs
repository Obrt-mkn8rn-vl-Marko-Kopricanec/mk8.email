using mk8.email.Configuration;

namespace mk8.email.Imap.Presentation;

internal sealed record ImapListenerConfig(
    string SmtpHostname,
    bool EnableImap,
    int ImapPort,
    bool EnableImapImplicitTls,
    int ImapImplicitTlsPort,
    bool EnableStartTls,
    string? TlsCertificatePath,
    string? TlsCertificateKeyPath,
    int MaxMessageSizeBytes,
    int ConnectionTimeoutSeconds,
    int MaxConnectionsPerIp)
{
    public static ImapListenerConfig From(EnvironmentConfig environment) => new(
        environment.Smtp.Hostname,
        environment.Imap.EnableImap,
        environment.Imap.Port,
        environment.Imap.EnableImplicitTls,
        environment.Imap.ImplicitTlsPort,
        environment.Smtp.EnableStartTls,
        environment.Tls.CertificatePath,
        environment.Tls.CertificateKeyPath,
        environment.Limits.MaxMessageSizeBytes,
        environment.Limits.ConnectionTimeoutSeconds,
        environment.Limits.MaxConnectionsPerIp);
}
