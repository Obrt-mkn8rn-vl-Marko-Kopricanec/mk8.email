namespace mk8.email.Gateway.Security;

public interface IAdminAuditLog
{
    Task WriteAsync(
        string actor,
        string action,
        string target,
        bool succeeded,
        string? remoteAddress,
        CancellationToken cancellationToken = default);
}
