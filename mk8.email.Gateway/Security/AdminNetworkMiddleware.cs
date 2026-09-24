namespace mk8.email.Gateway.Security;

public sealed class AdminNetworkMiddleware(
    RequestDelegate next,
    AdminNetworkPolicy policy,
    ILogger<AdminNetworkMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var address = context.Connection.RemoteIpAddress;
        if (address is null || !policy.Contains(address))
        {
            GatewaySecurityLog.RejectedAdminRequest(logger, address);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
