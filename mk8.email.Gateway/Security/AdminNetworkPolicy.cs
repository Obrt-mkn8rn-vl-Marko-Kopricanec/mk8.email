using System.Net;
using mk8.email.Configuration;

namespace mk8.email.Gateway.Security;

public sealed class AdminNetworkPolicy
{
    private readonly List<IpNetworkRange> _networks;

    public AdminNetworkPolicy(AdminConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var networks = new List<IpNetworkRange>();
        foreach (var value in config.AllowedNetworks)
        {
            if (!IpNetworkRange.TryParse(value, out var network))
                throw new InvalidOperationException($"The administrator network is not valid: {value}");
            networks.Add(network);
        }

        if (networks.Count == 0)
            throw new InvalidOperationException("Configure at least one administrator network.");

        _networks = networks;
    }

    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return _networks.Any(network => network.Contains(candidate));
    }
}
