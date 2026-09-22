using System.Text;
using System.Text.Json;
using mk8.email.Infrastructure.Environment;

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

        logger.LogInformation(
            "Administrator action {Action} by {Actor} on {Target} had result {Succeeded} from {RemoteAddress}",
            action,
            actor,
            target,
            succeeded,
            remoteAddress);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(config.AuditLogPath)
                ?? throw new InvalidOperationException("The audit log directory is not valid.");
            Directory.CreateDirectory(directory);

            await using var stream = new FileStream(
                config.AuditLogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
