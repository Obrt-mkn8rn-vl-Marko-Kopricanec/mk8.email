using mk8.email.Infrastructure.Models;
using Npgsql;
using System.Net;

namespace mk8.email.Infrastructure.Environment;

public sealed class EnvironmentConfig
{
    private const long JmapMaximumInteger = 9_007_199_254_740_991;

    public DatabaseConfig Database { get; init; } = new();
    public SmtpConfig Smtp { get; init; } = new();
    public ImapConfig Imap { get; init; } = new();
    public Pop3Config Pop3 { get; init; } = new();
    public SieveConfig Sieve { get; init; } = new();
    public JmapConfig Jmap { get; init; } = new();
    public DavConfig Dav { get; init; } = new();
    public OAuthConfig OAuth { get; init; } = new();
    public TlsConfig Tls { get; init; } = new();
    public DkimConfig Dkim { get; init; } = new();
    public SecurityConfig Security { get; init; } = new();
    public FilteringConfig Filtering { get; init; } = new();
    public QueueConfig Queue { get; init; } = new();
    public LimitsConfig Limits { get; init; } = new();
    public GeneralConfig General { get; init; } = new();
    public AdminConfig Admin { get; init; } = new();

    public string BuildConnectionString()
    {
        return new NpgsqlConnectionStringBuilder
        {
            Host = Database.Host,
            Port = Database.Port,
            Database = Database.Name,
            Username = Database.Username,
            Password = Database.Password,
            ApplicationName = "mk8.email",
        }.ConnectionString;
    }

    public IReadOnlyList<string> Validate(bool isDevelopment = false)
    {
        var errors = new List<string>();

        RequireValue(errors, Database.Host, "Database.Host is required.");
        RequirePort(errors, Database.Port, "Database.Port");
        RequireValue(errors, Database.Name, "Database.Name is required.");
        RequireValue(errors, Database.Username, "Database.Username is required.");
        RequireSecret(errors, Database.Password, "Database.Password", isDevelopment);

        if (Uri.CheckHostName(Smtp.Hostname) != UriHostNameType.Dns)
            errors.Add("Smtp.Hostname must be a valid DNS name.");
        if (!isDevelopment && !Smtp.Hostname.Contains('.'))
            errors.Add("Smtp.Hostname must be a fully qualified DNS name in production.");

        var enabledPorts = new List<(string Name, int Port)>();
        AddEnabledPort(enabledPorts, Smtp.EnableSmtp, "Smtp.Port", Smtp.Port);
        AddEnabledPort(enabledPorts, Smtp.EnableSubmission, "Smtp.SubmissionPort", Smtp.SubmissionPort);
        AddEnabledPort(enabledPorts, Smtp.EnableImplicitTls, "Smtp.ImplicitTlsPort", Smtp.ImplicitTlsPort);
        AddEnabledPort(enabledPorts, Imap.EnableImap, "Imap.Port", Imap.Port);
        AddEnabledPort(enabledPorts, Imap.EnableImplicitTls, "Imap.ImplicitTlsPort", Imap.ImplicitTlsPort);
        AddEnabledPort(enabledPorts, Pop3.EnablePop3, "Pop3.Port", Pop3.Port);
        AddEnabledPort(enabledPorts, Pop3.EnableImplicitTls, "Pop3.ImplicitTlsPort", Pop3.ImplicitTlsPort);
        AddEnabledPort(enabledPorts, Sieve.EnableManageSieve, "Sieve.Port", Sieve.Port);
        AddEnabledPort(
            enabledPorts,
            Jmap.EnableJmap || Dav.EnableDav || OAuth.EnableOAuth,
            "Jmap.Port",
            Jmap.Port);

        if (enabledPorts.Count == 0)
            errors.Add("Enable at least one SMTP, IMAP, POP3, ManageSieve, JMAP, DAV, or OAuth listener.");

        foreach (var enabledPort in enabledPorts)
            RequirePort(errors, enabledPort.Port, enabledPort.Name);

        foreach (var duplicate in enabledPorts.GroupBy(item => item.Port).Where(group => group.Count() > 1))
            errors.Add($"Enabled listeners cannot share port {duplicate.Key}.");

        if (Smtp.EnableSubmission && !Smtp.EnableStartTls)
            errors.Add("SMTP submission requires STARTTLS.");
        if (Smtp.AllowRelay && !Smtp.RequireAuth)
            errors.Add("SMTP relay requires authentication.");
        if (Smtp.RequireTls && !Smtp.EnableStartTls && !Smtp.EnableImplicitTls)
            errors.Add("Smtp.RequireTls requires STARTTLS or implicit TLS.");
        if (!isDevelopment && Imap.EnableImap && !Smtp.EnableStartTls)
            errors.Add("The production IMAP listener requires STARTTLS.");
        if (!isDevelopment && Pop3.EnablePop3 && !Pop3.EnableStartTls)
            errors.Add("The production POP3 listener requires STLS.");
        if (Sieve.EnableManageSieve && !Sieve.EnableStartTls)
            errors.Add("The ManageSieve listener requires STARTTLS.");

        if (Sieve.MaxScriptsPerUser is < 1 or > 1000)
            errors.Add("Sieve.MaxScriptsPerUser must be from 1 through 1000.");

        if (Jmap.IsDefault && !Jmap.EnableJmap)
            errors.Add("Jmap.IsDefault requires Jmap.EnableJmap.");
        if (Jmap.EnableJmap)
        {
            var publicBaseUrl = string.IsNullOrWhiteSpace(Jmap.PublicBaseUrl)
                ? $"https://{Smtp.Hostname}"
                : Jmap.PublicBaseUrl;
            if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var jmapBaseUri)
                || jmapBaseUri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(jmapBaseUri.Query)
                || !string.IsNullOrEmpty(jmapBaseUri.Fragment))
            {
                errors.Add("Jmap.PublicBaseUrl must be an absolute HTTPS URL without a query or fragment.");
            }
            if (Jmap.MaxUploadSizeBytes is < 1_048_576 or > 1_073_741_824)
                errors.Add("Jmap.MaxUploadSizeBytes must be from 1048576 through 1073741824.");
            if (Jmap.MaxRequestSizeBytes is < 65_536 or > 104_857_600)
                errors.Add("Jmap.MaxRequestSizeBytes must be from 65536 through 104857600.");
            if (Jmap.MaxCallsInRequest is < 1 or > 1024)
                errors.Add("Jmap.MaxCallsInRequest must be from 1 through 1024.");
            if (Jmap.MaxObjectsInGet is < 1 or > 10000)
                errors.Add("Jmap.MaxObjectsInGet must be from 1 through 10000.");
            if (Jmap.MaxObjectsInSet is < 1 or > 10000)
                errors.Add("Jmap.MaxObjectsInSet must be from 1 through 10000.");
            if (Jmap.MaxConcurrentRequests is < 1 or > 1024)
                errors.Add("Jmap.MaxConcurrentRequests must be from 1 through 1024.");
            if (Jmap.MaxConcurrentUploads is < 1 or > 128)
                errors.Add("Jmap.MaxConcurrentUploads must be from 1 through 128.");
            if (Jmap.UploadRetentionHours is < 1 or > 168)
                errors.Add("Jmap.UploadRetentionHours must be from 1 through 168.");
            if (Jmap.MaxUnreferencedBlobBytesPerAccount < Math.Max(
                    Jmap.MaxUploadSizeBytes,
                    Limits.MaxMessageSizeBytes)
                || Jmap.MaxUnreferencedBlobBytesPerAccount > JmapMaximumInteger)
            {
                errors.Add(
                    "Jmap.MaxUnreferencedBlobBytesPerAccount must be at least the larger of "
                    + "Jmap.MaxUploadSizeBytes and Limits.MaxMessageSizeBytes, and no greater than "
                    + "9007199254740991.");
            }
        }

        if (Dav.EnableDav)
        {
            if (Dav.MaxResourceSizeBytes is < 65_536 or > 107_374_1824)
                errors.Add("Dav.MaxResourceSizeBytes must be from 65536 through 1073741824.");
            if (Dav.MaxCollectionsPerUser is < 1 or > 1000)
                errors.Add("Dav.MaxCollectionsPerUser must be from 1 through 1000.");
            if (Dav.MaxResourcesPerCollection is < 1 or > 1_000_000)
                errors.Add("Dav.MaxResourcesPerCollection must be from 1 through 1000000.");
        }

        if (OAuth.AccessTokenMinutes is < 1 or > 60)
            errors.Add("OAuth.AccessTokenMinutes must be from 1 through 60.");
        if (OAuth.RefreshTokenDays is < 1 or > 365)
            errors.Add("OAuth.RefreshTokenDays must be from 1 through 365.");
        if (OAuth.AuthorizationCodeMinutes is < 1 or > 15)
            errors.Add("OAuth.AuthorizationCodeMinutes must be from 1 through 15.");
        if (OAuth.EnableOAuth)
        {
            if (string.IsNullOrEmpty(OAuth.ClientId)
                || OAuth.ClientId.Length > 128
                || OAuth.ClientId.Any(character => character is < '!' or > '~'))
            {
                errors.Add("OAuth.ClientId must contain from 1 through 128 visible ASCII characters.");
            }
            var publicBaseUrl = string.IsNullOrWhiteSpace(OAuth.PublicBaseUrl)
                ? Jmap.PublicBaseUrl ?? $"https://{Smtp.Hostname}"
                : OAuth.PublicBaseUrl;
            if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var oauthBaseUri)
                || oauthBaseUri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(oauthBaseUri.Query)
                || !string.IsNullOrEmpty(oauthBaseUri.Fragment))
            {
                errors.Add("OAuth.PublicBaseUrl must be an absolute HTTPS URL without a query or fragment.");
            }
        }

        var needsCertificate = Smtp.EnableStartTls
            || Smtp.EnableImplicitTls
            || Imap.EnableImplicitTls
            || Pop3.EnableStartTls
            || Pop3.EnableImplicitTls
            || (Sieve.EnableManageSieve && Sieve.EnableStartTls);
        if (needsCertificate)
            RequireFile(errors, Tls.CertificatePath, "Tls.CertificatePath");
        if (!string.IsNullOrWhiteSpace(Tls.CertificateKeyPath))
            RequireFile(errors, Tls.CertificateKeyPath, "Tls.CertificateKeyPath");
        if (Dkim.EnableSigning)
        {
            RequireFile(errors, Dkim.PrivateKeyPath, "Dkim.PrivateKeyPath");
            if (!DkimIdentityValidator.IsValidSelector(Dkim.Selector))
                errors.Add("Dkim.Selector must be a DNS label.");
        }

        if (!string.Equals(Security.PasswordHashScheme, "BLF-CRYPT", StringComparison.Ordinal))
            errors.Add("Security.PasswordHashScheme must be BLF-CRYPT.");
        if (!isDevelopment && (Security.EnableSpfCheck || Security.EnableDmarcCheck))
            errors.Add("The built-in SPF and DMARC checks are not approved for production.");

        if (!Uri.TryCreate(Filtering.RspamdEndpoint, UriKind.Absolute, out var rspamdEndpoint)
            || rspamdEndpoint.Scheme != Uri.UriSchemeHttp
            || rspamdEndpoint.AbsolutePath != "/checkv2")
        {
            errors.Add("Filtering.RspamdEndpoint must be an HTTP checkv2 URL.");
        }
        else if (!isDevelopment && !IsLoopbackHost(rspamdEndpoint.Host))
        {
            errors.Add("Filtering.RspamdEndpoint must use a loopback address in production.");
        }
        if (Filtering.TimeoutSeconds is < 5 or > 120)
            errors.Add("Filtering.TimeoutSeconds must be from 5 through 120.");

        if (Queue.PollIntervalMilliseconds is < 100 or > 60_000)
            errors.Add("Queue.PollIntervalMilliseconds must be from 100 through 60000.");
        if (Queue.LeaseSeconds is < 30 or > 3600)
            errors.Add("Queue.LeaseSeconds must be from 30 through 3600.");
        if (Queue.MaxAttempts is < 1 or > 100)
            errors.Add("Queue.MaxAttempts must be from 1 through 100.");
        if (Queue.MaxAgeHours is < 1 or > 720)
            errors.Add("Queue.MaxAgeHours must be from 1 through 720.");
        if (Queue.CompletedRetentionDays is < 1 or > 90)
            errors.Add("Queue.CompletedRetentionDays must be from 1 through 90.");

        if (Limits.MaxMessageSizeBytes < 64 * 1024)
            errors.Add("Limits.MaxMessageSizeBytes must be at least 65536.");
        if (Limits.MaxRecipientsPerMessage is < 1 or > 1000)
            errors.Add("Limits.MaxRecipientsPerMessage must be from 1 through 1000.");
        if (Limits.ConnectionTimeoutSeconds is < 10 or > 3600)
            errors.Add("Limits.ConnectionTimeoutSeconds must be from 10 through 3600.");
        if (Limits.MaxConnectionsPerIp is < 1 or > 10000)
            errors.Add("Limits.MaxConnectionsPerIp must be from 1 through 10000.");

        if (!isDevelopment && Admin.AllowedNetworks.Count == 0)
            errors.Add("Admin.AllowedNetworks must contain at least one network in production.");
        if (!isDevelopment && !Path.IsPathFullyQualified(Admin.DataProtectionKeyPath))
            errors.Add("Admin.DataProtectionKeyPath must be an absolute path in production.");
        if (!isDevelopment && !Path.IsPathFullyQualified(Admin.AuditLogPath))
            errors.Add("Admin.AuditLogPath must be an absolute path in production.");
        if (!isDevelopment && !Path.IsPathFullyQualified(Admin.HealthStatusPath))
            errors.Add("Admin.HealthStatusPath must be an absolute path in production.");
        if (Admin.SessionMinutes is < 5 or > 480)
            errors.Add("Admin.SessionMinutes must be from 5 through 480.");

        return errors;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static void AddEnabledPort(List<(string Name, int Port)> ports, bool enabled, string name, int port)
    {
        if (enabled)
            ports.Add((name, port));
    }

    private static void RequireFile(List<string> errors, string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"{name} is required.");
            return;
        }

        if (!File.Exists(Path.GetFullPath(path)))
            errors.Add($"{name} does not exist.");
    }

    private static void RequirePort(List<string> errors, int port, string name)
    {
        if (port is < 1 or > 65535)
            errors.Add($"{name} must be from 1 through 65535.");
    }

    private static void RequireSecret(List<string> errors, string value, string name, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{name} is required.");
            return;
        }

        if (!isDevelopment && value.Length < 16)
            errors.Add($"{name} must contain at least 16 characters in production.");
    }

    private static void RequireValue(List<string> errors, string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add(message);
    }

    public GlobalConfigDB ToGlobalConfig() => new()
    {
        SmtpHostname = Smtp.Hostname,
        SmtpPort = Smtp.Port,
        SmtpSubmissionPort = Smtp.SubmissionPort,
        SmtpImplicitTlsPort = Smtp.ImplicitTlsPort,
        EnableSmtp = Smtp.EnableSmtp,
        EnableSubmission = Smtp.EnableSubmission,
        EnableImplicitTls = Smtp.EnableImplicitTls,
        EnableStartTls = Smtp.EnableStartTls,
        RequireTls = Smtp.RequireTls,
        RequireAuth = Smtp.RequireAuth,
        AllowRelay = Smtp.AllowRelay,

        EnableImap = Imap.EnableImap,
        ImapPort = Imap.Port,
        EnableImapImplicitTls = Imap.EnableImplicitTls,
        ImapImplicitTlsPort = Imap.ImplicitTlsPort,

        TlsCertificatePath = Tls.CertificatePath,
        TlsCertificateKeyPath = Tls.CertificateKeyPath,

        DkimPrivateKeyPath = Dkim.PrivateKeyPath,
        DkimSelector = Dkim.Selector,
        EnableDkimSigning = Dkim.EnableSigning,

        EnableSpfCheck = Security.EnableSpfCheck,
        EnableDmarcCheck = Security.EnableDmarcCheck,
        PasswordHashScheme = Security.PasswordHashScheme,

        MaxMessageSizeBytes = Limits.MaxMessageSizeBytes,
        MaxRecipientsPerMessage = Limits.MaxRecipientsPerMessage,
        ConnectionTimeoutSeconds = Limits.ConnectionTimeoutSeconds,
        MaxConnectionsPerIp = Limits.MaxConnectionsPerIp,

        AllowRegistration = General.AllowRegistration,
    };
}

public sealed class DatabaseConfig
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Name { get; init; } = "mk8email";
    public string Username { get; init; } = "postgres";
    public string Password { get; set; } = string.Empty;
    public string? PasswordFile { get; init; }
}

public sealed class SmtpConfig
{
    public string Hostname { get; init; } = "localhost";
    public int Port { get; init; } = 25;
    public int SubmissionPort { get; init; } = 587;
    public int ImplicitTlsPort { get; init; } = 465;
    public bool EnableSmtp { get; init; } = true;
    public bool EnableSubmission { get; init; }
    public bool EnableImplicitTls { get; init; }
    public bool EnableStartTls { get; init; }
    public bool RequireTls { get; init; }
    public bool RequireAuth { get; init; } = true;
    public bool AllowRelay { get; init; }
}

public sealed class ImapConfig
{
    public int Port { get; init; } = 143;
    public int ImplicitTlsPort { get; init; } = 993;
    public bool EnableImap { get; init; } = true;
    public bool EnableImplicitTls { get; init; }
}

public sealed class Pop3Config
{
    public int Port { get; init; } = 110;
    public int ImplicitTlsPort { get; init; } = 995;
    public bool EnablePop3 { get; init; }
    public bool EnableImplicitTls { get; init; }
    public bool EnableStartTls { get; init; }
}

public sealed class SieveConfig
{
    public int Port { get; init; } = 4190;
    public bool EnableManageSieve { get; init; }
    public bool EnableStartTls { get; init; } = true;
    public int MaxScriptsPerUser { get; init; } = 64;
}

public sealed class JmapConfig
{
    public int Port { get; init; } = 8081;
    public bool EnableJmap { get; init; } = true;
    public bool IsDefault { get; init; } = true;
    public string? PublicBaseUrl { get; init; }
    public long MaxUploadSizeBytes { get; init; } = 50_000_000;
    public long MaxRequestSizeBytes { get; init; } = 10_000_000;
    public int MaxCallsInRequest { get; init; } = 64;
    public int MaxObjectsInGet { get; init; } = 500;
    public int MaxObjectsInSet { get; init; } = 500;
    public int MaxConcurrentRequests { get; init; } = 8;
    public int MaxConcurrentUploads { get; init; } = 4;
    public int UploadRetentionHours { get; init; } = 24;
    public long MaxUnreferencedBlobBytesPerAccount { get; init; } = 100_000_000;

    public Uri GetPublicBaseUri(string smtpHostname)
    {
        var value = string.IsNullOrWhiteSpace(PublicBaseUrl)
            ? $"https://{smtpHostname}"
            : PublicBaseUrl;
        return new Uri(value.TrimEnd('/') + "/", UriKind.Absolute);
    }
}

public sealed class DavConfig
{
    public bool EnableDav { get; init; } = true;
    public int MaxResourceSizeBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxCollectionsPerUser { get; init; } = 100;
    public int MaxResourcesPerCollection { get; init; } = 100_000;
}

public sealed class OAuthConfig
{
    public bool EnableOAuth { get; init; }
    public string? PublicBaseUrl { get; init; }
    public string ClientId { get; init; } = "thunderbird";
    public int AccessTokenMinutes { get; init; } = 10;
    public int RefreshTokenDays { get; init; } = 90;
    public int AuthorizationCodeMinutes { get; init; } = 5;

    public Uri GetPublicBaseUri(string smtpHostname, string? jmapPublicBaseUrl)
    {
        var value = string.IsNullOrWhiteSpace(PublicBaseUrl)
            ? jmapPublicBaseUrl ?? $"https://{smtpHostname}"
            : PublicBaseUrl;
        return new Uri(value.TrimEnd('/') + "/", UriKind.Absolute);
    }
}

public sealed class TlsConfig
{
    public string? CertificatePath { get; init; }
    public string? CertificateKeyPath { get; init; }
}

public sealed class DkimConfig
{
    public string? PrivateKeyPath { get; init; }
    public string Selector { get; init; } = "default";
    public bool EnableSigning { get; init; }
}

public sealed class SecurityConfig
{
    public bool EnableSpfCheck { get; init; }
    public bool EnableDmarcCheck { get; init; }
    public string PasswordHashScheme { get; init; } = "BLF-CRYPT";
}

public sealed class FilteringConfig
{
    public string RspamdEndpoint { get; init; } = "http://127.0.0.1:11333/checkv2";
    public int TimeoutSeconds { get; init; } = 70;
}

public sealed class QueueConfig
{
    public int PollIntervalMilliseconds { get; init; } = 500;
    public int LeaseSeconds { get; init; } = 300;
    public int MaxAttempts { get; init; } = 20;
    public int MaxAgeHours { get; init; } = 120;
    public int CompletedRetentionDays { get; init; } = 14;
}

public sealed class LimitsConfig
{
    public int MaxMessageSizeBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxRecipientsPerMessage { get; init; } = 100;
    public int ConnectionTimeoutSeconds { get; init; } = 300;
    public int MaxConnectionsPerIp { get; init; } = 10;
}

public sealed class GeneralConfig
{
    public bool AllowRegistration { get; init; }
}

public sealed class AdminConfig
{
    public IReadOnlyList<string> AllowedNetworks { get; init; } = [];
    public string DataProtectionKeyPath { get; init; } = "data-protection";
    public string AuditLogPath { get; init; } = "audit/admin.jsonl";
    public string HealthStatusPath { get; init; } = "health/status.json";
    public int SessionMinutes { get; init; } = 30;
}
