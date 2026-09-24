using Microsoft.Extensions.Logging;

namespace mk8.email.Gateway.Protocols;

internal static partial class GatewayProtocolLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "Gateway presentation request processing failed")]
    public static partial void PresentationProcessingFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "Gateway presentation request {RequestId} failed unexpectedly")]
    public static partial void PresentationUnexpectedFailure(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "Gateway presentation request {RequestId} failed")]
    public static partial void PresentationFailed(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Could not journal the presentation failure for {RequestId}")]
    public static partial void PresentationJournalFailure(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Could not fail presentation request {RequestId}")]
    public static partial void PresentationCompletionFailure(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error,
        Message = "Could not persist {Direction} {Protocol} presentation traffic")]
    public static partial void TrafficJournalFailure(
        ILogger logger,
        Exception exception,
        string direction,
        string protocol);
}
