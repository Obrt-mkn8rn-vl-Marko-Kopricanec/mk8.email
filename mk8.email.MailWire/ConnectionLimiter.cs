using System.Collections.Concurrent;
using System.Net;

namespace mk8.email.MailWire;

public sealed class ConnectionLimiter(int maximumConnections)
{
    private readonly ConcurrentDictionary<IPAddress, int> _connectionsPerAddress = new();
    private readonly SemaphoreSlim _slots = new(maximumConnections, maximumConnections);

    public IDisposable? TryAcquire(IPAddress address, int maximumConnectionsPerAddress)
    {
        if (!_slots.Wait(0))
            return null;

        var addressCount = _connectionsPerAddress.AddOrUpdate(
            address,
            1,
            static (_, current) => current + 1);
        if (addressCount <= maximumConnectionsPerAddress)
            return new ConnectionLease(this, address);

        Release(address);
        return null;
    }

    private void Release(IPAddress address)
    {
        while (_connectionsPerAddress.TryGetValue(address, out var current))
        {
            if (current <= 1)
            {
                var pair = new KeyValuePair<IPAddress, int>(address, current);
                if (((ICollection<KeyValuePair<IPAddress, int>>)_connectionsPerAddress).Remove(pair))
                    break;
            }
            else if (_connectionsPerAddress.TryUpdate(address, current - 1, current))
            {
                break;
            }
        }

        _slots.Release();
    }

    private sealed class ConnectionLease(
        ConnectionLimiter owner,
        IPAddress address) : IDisposable
    {
        private ConnectionLimiter? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(address);
        }
    }
}
