using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;

namespace mk8.email.Application.Services;

public class SeederService(
    EmailDbContext db,
    EnvironmentConfig env) : ISeederService
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SyncGlobalConfigAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncGlobalConfigAsync(CancellationToken cancellationToken)
    {
        var config = await db.GlobalConfig.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (config is null)
            throw new InvalidOperationException("The database schema is not initialized.");

        config.SmtpHostname = env.Smtp.Hostname;
        config.SmtpPort = env.Smtp.Port;
        config.SmtpSubmissionPort = env.Smtp.SubmissionPort;
        config.SmtpImplicitTlsPort = env.Smtp.ImplicitTlsPort;
        config.EnableSmtp = env.Smtp.EnableSmtp;
        config.EnableSubmission = env.Smtp.EnableSubmission;
        config.EnableImplicitTls = env.Smtp.EnableImplicitTls;
        config.EnableStartTls = env.Smtp.EnableStartTls;
        config.RequireTls = env.Smtp.RequireTls;
        config.RequireAuth = env.Smtp.RequireAuth;
        config.AllowRelay = env.Smtp.AllowRelay;

        config.EnableImap = env.Imap.EnableImap;
        config.ImapPort = env.Imap.Port;
        config.EnableImapImplicitTls = env.Imap.EnableImplicitTls;
        config.ImapImplicitTlsPort = env.Imap.ImplicitTlsPort;

        config.TlsCertificatePath = env.Tls.CertificatePath;
        config.TlsCertificateKeyPath = env.Tls.CertificateKeyPath;

        config.DkimPrivateKeyPath = env.Dkim.PrivateKeyPath;
        config.DkimSelector = env.Dkim.Selector;
        config.EnableDkimSigning = env.Dkim.EnableSigning;

        config.EnableSpfCheck = env.Security.EnableSpfCheck;
        config.EnableDmarcCheck = env.Security.EnableDmarcCheck;
        config.PasswordHashScheme = env.Security.PasswordHashScheme;

        config.MaxMessageSizeBytes = env.Limits.MaxMessageSizeBytes;
        config.MaxRecipientsPerMessage = env.Limits.MaxRecipientsPerMessage;
        config.ConnectionTimeoutSeconds = env.Limits.ConnectionTimeoutSeconds;
        config.MaxConnectionsPerIp = env.Limits.MaxConnectionsPerIp;

        config.AllowRegistration = env.General.AllowRegistration;

        config.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
