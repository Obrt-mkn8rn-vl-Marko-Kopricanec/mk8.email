using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure;

public static class EnvironmentConfigInfrastructureExtensions
{
    public static GlobalConfigDB ToGlobalConfig(this EnvironmentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new GlobalConfigDB
        {
            SmtpHostname = config.Smtp.Hostname,
            SmtpPort = config.Smtp.Port,
            SmtpSubmissionPort = config.Smtp.SubmissionPort,
            SmtpImplicitTlsPort = config.Smtp.ImplicitTlsPort,
            EnableSmtp = config.Smtp.EnableSmtp,
            EnableSubmission = config.Smtp.EnableSubmission,
            EnableImplicitTls = config.Smtp.EnableImplicitTls,
            EnableStartTls = config.Smtp.EnableStartTls,
            RequireTls = config.Smtp.RequireTls,
            RequireAuth = config.Smtp.RequireAuth,
            AllowRelay = config.Smtp.AllowRelay,

            EnableImap = config.Imap.EnableImap,
            ImapPort = config.Imap.Port,
            EnableImapImplicitTls = config.Imap.EnableImplicitTls,
            ImapImplicitTlsPort = config.Imap.ImplicitTlsPort,

            TlsCertificatePath = config.Tls.CertificatePath,
            TlsCertificateKeyPath = config.Tls.CertificateKeyPath,

            DkimPrivateKeyPath = config.Dkim.PrivateKeyPath,
            DkimSelector = config.Dkim.Selector,
            EnableDkimSigning = config.Dkim.EnableSigning,

            EnableSpfCheck = config.Security.EnableSpfCheck,
            EnableDmarcCheck = config.Security.EnableDmarcCheck,
            PasswordHashScheme = config.Security.PasswordHashScheme,

            MaxMessageSizeBytes = config.Limits.MaxMessageSizeBytes,
            MaxRecipientsPerMessage = config.Limits.MaxRecipientsPerMessage,
            ConnectionTimeoutSeconds = config.Limits.ConnectionTimeoutSeconds,
            MaxConnectionsPerIp = config.Limits.MaxConnectionsPerIp,

            AllowRegistration = config.General.AllowRegistration,
        };
    }
}
