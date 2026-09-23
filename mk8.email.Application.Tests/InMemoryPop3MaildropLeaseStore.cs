using mk8.email.Messaging;

namespace mk8.email.Application.Tests;

internal sealed class InMemoryPop3MaildropLeaseStore : IPop3MaildropLeaseStore
{
    private readonly Dictionary<Guid, Pop3MaildropLease> owners = [];
    private readonly Lock gate = new();

    public Task<Pop3MaildropLease?> TryAcquireAsync(
        Guid userId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (owners.ContainsKey(userId))
                return Task.FromResult<Pop3MaildropLease?>(null);
            var lease = new Pop3MaildropLease(userId, Guid.CreateVersion7());
            owners.Add(userId, lease);
            return Task.FromResult<Pop3MaildropLease?>(lease);
        }
    }

    public Task<bool> RenewAsync(
        Pop3MaildropLease lease,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(owners.TryGetValue(lease.UserId, out var owner)
                && owner.OwnerToken == lease.OwnerToken);
        }
    }

    public Task ReleaseAsync(
        Pop3MaildropLease lease,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (owners.TryGetValue(lease.UserId, out var owner)
                && owner.OwnerToken == lease.OwnerToken)
            {
                owners.Remove(lease.UserId);
            }
            return Task.CompletedTask;
        }
    }
}
