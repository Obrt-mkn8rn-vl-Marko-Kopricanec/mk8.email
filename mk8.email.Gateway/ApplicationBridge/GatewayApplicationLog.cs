using Microsoft.Extensions.Logging;

namespace mk8.email.Gateway.ApplicationBridge;

internal static partial class GatewayApplicationLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Application operation failed at the Gateway boundary with code {Code}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string code);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "The mail health snapshot could not be read from {StatusPath}")]
    public static partial void MailHealthUnavailable(ILogger logger, Exception exception, string statusPath);
}
