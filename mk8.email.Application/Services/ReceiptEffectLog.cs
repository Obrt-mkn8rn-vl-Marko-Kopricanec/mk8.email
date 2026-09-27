using Microsoft.Extensions.Logging;

namespace mk8.email.Application.Services;

internal static partial class ReceiptEffectLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Durable effect dispatch failed for operation receipt {ReceiptId}; retry remains scheduled.")]
    public static partial void DispatchFailed(ILogger logger, Guid receiptId, Exception exception);
}
