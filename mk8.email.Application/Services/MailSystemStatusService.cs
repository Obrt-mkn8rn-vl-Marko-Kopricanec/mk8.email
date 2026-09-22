using System.Text.Json;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Configuration;

namespace mk8.email.Application.Services;

public sealed class MailSystemStatusService(
    AdminConfig config,
    ILogger<MailSystemStatusService> logger) : IMailSystemStatusService
{
    private const long MaximumStatusFileBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<MailSystemStatusDTO> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(config.HealthStatusPath);
        try
        {
            var information = new FileInfo(path);
            if (!information.Exists || information.Length is <= 0 or > MaximumStatusFileBytes)
                return MailSystemStatusDTO.Unavailable;

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var status = await JsonSerializer.DeserializeAsync<MailSystemStatusDTO>(
                stream,
                JsonOptions,
                cancellationToken);

            return IsValid(status) ? status! : MailSystemStatusDTO.Unavailable;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            logger.LogWarning(exception, "The mail health snapshot could not be read from {StatusPath}", path);
            return MailSystemStatusDTO.Unavailable;
        }
    }

    private static bool IsValid(MailSystemStatusDTO? status)
    {
        if (status is null
            || status.State is not ("healthy" or "unhealthy")
            || status.CheckedAt is null
            || status.ErrorCount < 0
            || status.QueueCount is < 0
            || status.OldestQueuedMessageSeconds is < 0
            || status.MailStorageUsedPercent is < 0 or > 100
            || status.BackupExportAgeSeconds is < 0
            || status.ClamSignatureAgeSeconds is < 0)
        {
            return false;
        }

        return true;
    }
}
