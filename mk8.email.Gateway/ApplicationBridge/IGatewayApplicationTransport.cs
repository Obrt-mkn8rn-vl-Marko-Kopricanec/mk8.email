namespace mk8.email.Gateway.ApplicationBridge;

public interface IGatewayApplicationTransport
{
    Task<TResponse> SendAsync<TRequest, TResponse>(
        string protocol,
        string operation,
        TRequest value,
        CancellationToken cancellationToken = default);
}

public sealed class GatewayApplicationException(
    string code,
    string message,
    bool isUnavailable = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool IsUnavailable { get; } = isUnavailable;
}

public sealed record GatewayApplicationOptions(
    string InstanceId,
    TimeSpan RequestTimeout,
    TimeSpan TrafficJournalTimeout);
