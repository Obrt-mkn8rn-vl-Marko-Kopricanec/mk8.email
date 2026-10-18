using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace mk8.email.Configuration;

/// <summary>Management-only values in the CLI installation's protected startup snapshot.</summary>
public sealed class ManagementConfig
{
    public string? AccountPassword { get; init; }
    public string? TotpCode { get; init; }
    public string? PgDumpExecutablePath { get; init; }
    public string? PgRestoreExecutablePath { get; init; }
    public string? ArchiveConnectionString { get; init; }
    public string? ArchiveContainerName { get; init; }
    public string? ArchiveVerificationKeyPem { get; init; }
    public string? WakeDatabaseHost { get; init; }
    public int WakeDatabasePort { get; init; }
    public string? WakeDatabaseName { get; init; }
    public string? WakeDatabaseUsername { get; init; }

    [JsonIgnore]
    internal bool HasValues => AccountPassword is not null || TotpCode is not null
        || PgDumpExecutablePath is not null || PgRestoreExecutablePath is not null || ArchiveConnectionString is not null
        || ArchiveContainerName is not null || ArchiveVerificationKeyPem is not null
        || WakeDatabaseHost is not null || WakeDatabasePort != 0
        || WakeDatabaseName is not null || WakeDatabaseUsername is not null;

    public string RequireAccountPassword()
    {
        if (string.IsNullOrWhiteSpace(AccountPassword) || AccountPassword.Length > 1024)
            throw new InvalidOperationException("Management.AccountPassword is missing or invalid.");
        return AccountPassword;
    }

    public string RequireTotpCode()
    {
        var code = TotpCode?.Trim();
        if (code is null || code.Length != 6 || code.Any(character => character is < '0' or > '9'))
            throw new InvalidOperationException("Management.TotpCode must contain exactly six ASCII digits.");
        return code;
    }

    public string RequirePgDumpExecutable()
    {
        if (string.IsNullOrWhiteSpace(PgDumpExecutablePath)
            || !Path.IsPathFullyQualified(PgDumpExecutablePath) || !File.Exists(PgDumpExecutablePath))
        {
            throw new InvalidOperationException("Management.PgDumpExecutablePath must name an explicit absolute existing file.");
        }
        return PgDumpExecutablePath;
    }

    public string RequirePgRestoreExecutable()
    {
        if (string.IsNullOrWhiteSpace(PgRestoreExecutablePath)
            || !Path.IsPathFullyQualified(PgRestoreExecutablePath) || !File.Exists(PgRestoreExecutablePath))
        {
            throw new InvalidOperationException("Management.PgRestoreExecutablePath must name an explicit absolute existing file.");
        }
        return PgRestoreExecutablePath;
    }

    /// <summary>Returns a new owned public RSA verifier; no key-file fallback is consulted.</summary>
    public RSA CreateArchiveVerifier()
    {
        if (string.IsNullOrWhiteSpace(ArchiveVerificationKeyPem) || ArchiveVerificationKeyPem.Length > 65_536
            || !ArchiveVerificationKeyPem.Trim().StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
        {
            throw new CryptographicException("The archive public verification value is missing or invalid.");
        }
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(ArchiveVerificationKeyPem);
            if (key.KeySize < 3072)
                throw new CryptographicException("The archive public verification value is too weak.");
            return key;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new CryptographicException("The archive public verification value is invalid or too weak.");
        }
    }
}
