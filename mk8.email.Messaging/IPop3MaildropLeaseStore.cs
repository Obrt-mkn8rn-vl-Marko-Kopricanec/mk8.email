namespace mk8.email.Messaging;

public interface IPop3MaildropLeaseStore
{
    Task<Pop3MaildropLease?> TryAcquireAsync(
        Guid userId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    Task<bool> RenewAsync(
        Pop3MaildropLease lease,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(
        Pop3MaildropLease lease,
        CancellationToken cancellationToken = default);
}
