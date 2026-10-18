using System.Net;
using Npgsql;

namespace mk8.email.Configuration;

/// <summary>One installation-local Wake role snapshot; no connection-file or process-environment fallback.</summary>
public sealed class WakeConfig
{
    public string DatabaseHost { get; init; } = string.Empty;
    public int DatabasePort { get; init; }
    public string DatabaseName { get; init; } = string.Empty;
    public string DatabaseUsername { get; init; } = string.Empty;
    public string DatabasePassword { get; init; } = string.Empty;
    public SslMode DatabaseSslMode { get; init; } = SslMode.VerifyFull;
    public string DotnetHostPath { get; init; } = string.Empty;
    public string WorkerAssemblyPath { get; init; } = string.Empty;
    public bool IncludeJmap { get; init; } = true;

    public string BuildConnectionString()
    {
        Validate();
        return new NpgsqlConnectionStringBuilder
        {
            Host = DatabaseHost,
            Port = DatabasePort,
            Database = DatabaseName,
            Username = DatabaseUsername,
            Password = DatabasePassword,
            SslMode = DatabaseSslMode,
            ApplicationName = "mk8.email.wake",
        }.ConnectionString;
    }

    internal void Validate()
    {
        if (Uri.CheckHostName(DatabaseHost) == UriHostNameType.Unknown
            || DatabasePort is < 1 or > 65535
            || string.IsNullOrWhiteSpace(DatabaseName)
            || string.IsNullOrWhiteSpace(DatabaseUsername)
            || string.IsNullOrWhiteSpace(DatabasePassword)
            || DatabasePassword.Length < 16)
            throw new InvalidOperationException("Wake requires explicit valid database host, port, name, user and password values.");
        var loopback = IPAddress.TryParse(DatabaseHost, out var address) && IPAddress.IsLoopback(address);
        if (DatabaseSslMode != SslMode.VerifyFull && !(loopback && DatabaseSslMode == SslMode.Disable))
            throw new InvalidOperationException("Wake requires verified database TLS except for explicitly configured literal loopback.");
        if (!Path.IsPathFullyQualified(DotnetHostPath) || !Path.IsPathFullyQualified(WorkerAssemblyPath))
            throw new InvalidOperationException("Wake requires explicit absolute dotnet-host and Worker assembly paths.");
    }
}
