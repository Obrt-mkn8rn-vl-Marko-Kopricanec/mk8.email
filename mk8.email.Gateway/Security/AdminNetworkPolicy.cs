using System.Net;
using mk8.email.Configuration;

namespace mk8.email.Gateway.Security;

public sealed class AdminNetworkPolicy
{
    private readonly IReadOnlyList<IpNetworkRange> _networks;

    public AdminNetworkPolicy(AdminConfig config)
    {
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
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return _networks.Any(network => network.Contains(candidate));
    }
}

internal sealed class IpNetworkRange
{
    private readonly byte[] _network;
    private readonly int _prefixLength;

    private IpNetworkRange(byte[] network, int prefixLength)
    {
        _network = network;
        _prefixLength = prefixLength;
    }

    public static bool TryParse(string value, out IpNetworkRange network)
    {
        network = null!;
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var address)
            || !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var bytes = normalized.GetAddressBytes();
        if (prefixLength < 0 || prefixLength > bytes.Length * 8)
            return false;

        var networkBytes = bytes.ToArray();
        ApplyMask(networkBytes, prefixLength);
        network = new IpNetworkRange(networkBytes, prefixLength);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != _network.Length)
            return false;

        ApplyMask(bytes, _prefixLength);
        return bytes.AsSpan().SequenceEqual(_network);
    }

    private static void ApplyMask(byte[] bytes, int prefixLength)
    {
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (remainingBits > 0)
        {
            bytes[fullBytes] &= (byte)(0xff << (8 - remainingBits));
            fullBytes++;
        }

        Array.Clear(bytes, fullBytes, bytes.Length - fullBytes);
    }
}
