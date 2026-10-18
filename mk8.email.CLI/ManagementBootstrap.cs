using System.Net;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using mk8.email.Configuration;

namespace mk8.email.CLI;

internal static class ManagementBootstrap
{
    internal static HostApplicationBuilder CreateBuilder() => Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = [],
        EnvironmentName = Environments.Production,
        ApplicationName = typeof(ManagementBootstrap).Assembly.GetName().Name,
        ContentRootPath = AppContext.BaseDirectory,
    });

    internal static bool IsSupported(string[] arguments) => arguments switch
    {
        ["--healthcheck" or "--initialize-empty-database" or "--ensure-runtime-schema"
            or "--validate-gateway-config" or "--validate-worker-config" or "--healthcheck-gateway"
            or "--probe-gateway-backends" or "--probe-worker-backends" or "--probe-worker-dispatch"
            or "--probe-gateway-dispatch" or "--audit-blob-references" or "--audit-messaging-keys"
            or "--worker-wake-schema-state" or "--prepare-worker-wake"] => true,
        ["--list-app-passwords" or "--enroll-totp" or "--confirm-totp" or "--totp-status"
            or "--regenerate-recovery-codes" or "--disable-totp" or "--export-distributed-snapshot"
            or "--restore-distributed-snapshot" or "--verify-distributed-snapshot", _] => true,
        ["--restore-worker-wake", "legacy" or "receipts"] => true,
        ["--ensure-domain" or "--create-account" or "--set-catchall" or "--set-domain-active"
            or "--create-app-password" or "--revoke-app-password" or "--purge-quarantined-smoke-message"
            or "--publish-distributed-archive" or "--fetch-distributed-archive", _, _] => true,
        _ => false,
    };

    internal static EnvironmentValidationRole SelectRole(string command) => command is
        "--healthcheck" or "--validate-gateway-config" or "--healthcheck-gateway"
        or "--probe-gateway-backends" or "--probe-gateway-dispatch"
            ? EnvironmentValidationRole.Gateway : EnvironmentValidationRole.ApplicationWorker;

    internal static BlobServiceClient CreateArchiveClient(ManagementConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.ArchiveConnectionString)
            || configuration.ArchiveConnectionString.Length > 16_384)
        {
            throw new InvalidOperationException("Management.ArchiveConnectionString is missing or invalid.");
        }
        var container = configuration.ArchiveContainerName;
        if (container is null || container.Length is < 3 or > 63 || container[0] == '-' || container[^1] == '-'
            || container.Contains("--", StringComparison.Ordinal)
            || container.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw new InvalidOperationException("Management.ArchiveContainerName is missing or invalid.");
        }
        var client = new BlobServiceClient(configuration.ArchiveConnectionString);
        var address = client.Uri;
        var loopback = IPAddress.TryParse(address.Host, out var literal) && IPAddress.IsLoopback(literal);
        if (!string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Fragment)
            || (!string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                && !(loopback && string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("Archive transport requires verified HTTPS or explicit literal loopback HTTP.");
        }
        return client;
    }
}
