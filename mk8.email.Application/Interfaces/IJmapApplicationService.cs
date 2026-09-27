using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Interfaces;

public interface IJmapApplicationService
{
    Task<JmapApplicationResult> GetProfileAsync(
        JmapProfileApplicationRequest request,
        CancellationToken cancellationToken = default);

    Task<JmapApplicationResult> ExecuteBatchAsync(
        JmapBatchApplicationRequest request,
        CancellationToken cancellationToken = default);

    Task<JmapApplicationResult> UploadAsync(
        JmapUploadApplicationRequest request,
        CancellationToken cancellationToken = default);

    Task<JmapApplicationResult> DownloadAsync(
        JmapDownloadApplicationRequest request,
        CancellationToken cancellationToken = default);

    Task<JmapApplicationResult> PollChangesAsync(
        JmapChangesApplicationRequest request,
        CancellationToken cancellationToken = default);
}
