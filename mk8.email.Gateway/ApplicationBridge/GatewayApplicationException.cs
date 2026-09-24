namespace mk8.email.Gateway.ApplicationBridge;

public sealed class GatewayApplicationException(
    string code,
    string message,
    bool isUnavailable = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool IsUnavailable { get; } = isUnavailable;

    public GatewayApplicationException()
        : this("application-error", "The application request failed.")
    {
    }

    public GatewayApplicationException(string message)
        : this("application-error", message)
    {
    }

    public GatewayApplicationException(string message, Exception innerException)
        : this("application-error", message, innerException: innerException)
    {
    }
}
