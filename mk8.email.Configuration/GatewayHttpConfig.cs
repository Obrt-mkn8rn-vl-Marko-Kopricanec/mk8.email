using System.Net;

namespace mk8.email.Configuration;

/// <summary>Explicit loopback HTTP backend settings; public TLS routing is independently gated.</summary>
public sealed class GatewayHttpConfig
{
    public string ListenAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];
    public int? HttpsRedirectPort { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IPAddress.TryParse(ListenAddress, out var address) || !IPAddress.IsLoopback(address))
            errors.Add("Http.ListenAddress must be a literal loopback address for the plaintext backend.");
        if (Port is < 1 or > 65535)
            errors.Add("Http.Port must be from 1 through 65535.");
        if (HttpsRedirectPort is < 1 or > 65535)
            errors.Add("Http.HttpsRedirectPort must be from 1 through 65535 when configured.");
        if (AllowedHosts.Count == 0 || AllowedHosts.Count > 64)
            errors.Add("Http.AllowedHosts must contain from 1 through 64 explicit host names or IP literals.");
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicate = false;
        foreach (var host in AllowedHosts)
        {
            if (!hosts.Add(host)) duplicate = true;
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253
                || host.Contains('*', StringComparison.Ordinal)
                || IsUnspecifiedAddress(host)
                || host.Any(char.IsWhiteSpace) || host.Any(char.IsControl)
                || Uri.CheckHostName(host) is UriHostNameType.Unknown)
            {
                errors.Add("Http.AllowedHosts entries must be exact host names or IP literals, without wildcards or ports.");
            }
        }
        if (duplicate)
            errors.Add("Http.AllowedHosts entries must be unique.");
        return errors;
    }

    private static bool IsUnspecifiedAddress(string host) =>
        IPAddress.TryParse(host, out var address)
        && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));
}
