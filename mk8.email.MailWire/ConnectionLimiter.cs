using System.Collections.Concurrent;
using System.Net;

namespace mk8.email.MailWire;

public sealed class ConnectionLimiter
{
    private readonly ConcurrentDictionary<IPAddress, int> _connectionsPerAddress = new();
    private int _availableSlots;

    public ConnectionLimiter(int maximumConnections)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumConnections);
        _availableSlots = maximumConnections;
    }

    public IDisposable? TryAcquire(IPAddress address, int maximumConnectionsPerAddress)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!TryReserveSlot())
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

    private bool TryReserveSlot()
    {
        while (true)
        {
            var remaining = Volatile.Read(ref _availableSlots);
            if (remaining == 0)
                return false;
            if (Interlocked.CompareExchange(ref _availableSlots, remaining - 1, remaining) == remaining)
                return true;
        }
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

        Interlocked.Increment(ref _availableSlots);
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
