using System.Text;
using System.Text.Json;
using mk8.email.Configuration;

namespace mk8.email.Gateway.Security;

public sealed class AdminAuditLog(
    AdminConfig config,
    ILogger<AdminAuditLog> logger) : IAdminAuditLog, IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task WriteAsync(
        string actor,
        string action,
        string target,
        bool succeeded,
        string? remoteAddress,
        CancellationToken cancellationToken = default)
    {
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            actor,
            action,
            target,
            succeeded,
            remoteAddress,
        };
        var line = JsonSerializer.Serialize(entry) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        GatewaySecurityLog.AdminAction(logger, action, actor, target, succeeded, remoteAddress);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(config.AuditLogPath)
                ?? throw new InvalidOperationException("The audit log directory is not valid.");
            Directory.CreateDirectory(directory);

            var stream = new FileStream(
                config.AuditLogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using var streamLifetime = stream.ConfigureAwait(false);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            // Audit durability requires a disk flush; FlushAsync alone need not fsync.
#pragma warning disable CA1849
            stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
