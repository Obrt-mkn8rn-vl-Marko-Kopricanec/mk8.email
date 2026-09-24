namespace mk8.email.Gateway.ApplicationBridge;

public interface IGatewayApplicationTransport
{
    Task<TResponse> SendAsync<TRequest, TResponse>(
        string protocol,
        string operation,
        TRequest value,
        CancellationToken cancellationToken = default);
}
