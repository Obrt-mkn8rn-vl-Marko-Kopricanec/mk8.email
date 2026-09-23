using System.Text.Json;
using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;

namespace mk8.email.Hosting;

public static class DistributedApplicationProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task ProbeAsync(
        IApplicationRequestClient requests,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(timeout);
        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.CreateVersion7();
        var request = new ApplicationRequest(
            requestId,
            Guid.CreateVersion7(),
            0,
            "health",
            ApplicationOperations.SystemPing,
            "application/json",
            "{}"u8.ToArray(),
            new Dictionary<string, string>(),
            now,
            now.Add(timeout),
            requestId.ToString("N"));
        var response = await requests.SendAsync(request, probeCancellation.Token);
        if (response.RequestId != requestId
            || response.IsError
            || !string.Equals(response.ContentType, "application/json", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Application Worker returned an invalid probe response.");
        }

        SystemPingResult? result;
        try
        {
            result = JsonSerializer.Deserialize<SystemPingResult>(response.Payload, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The Application Worker returned an invalid probe payload.",
                exception);
        }
        if (result is null || result.RespondedAt == default)
            throw new InvalidOperationException("The Application Worker returned an empty probe result.");
    }
}
