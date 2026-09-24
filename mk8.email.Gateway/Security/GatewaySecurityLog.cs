using System.Net;
using Microsoft.Extensions.Logging;

namespace mk8.email.Gateway.Security;

internal static partial class GatewaySecurityLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Administrator action {Action} by {Actor} on {Target} had result {Succeeded} from {RemoteAddress}")]
    public static partial void AdminAction(
        ILogger logger,
        string action,
        string actor,
        string target,
        bool succeeded,
        string? remoteAddress);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Rejected an administrator request from {RemoteAddress}")]
    public static partial void RejectedAdminRequest(ILogger logger, IPAddress? remoteAddress);
}
