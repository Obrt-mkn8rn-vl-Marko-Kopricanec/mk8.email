using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.ApplicationBridge;

public static class GatewayApplicationHealth
{
    public static async Task<IResult> CheckAsync(
        IGatewayApplicationTransport transport,
        CancellationToken cancellationToken)
    {
        var response = await transport.SendAsync<object, SystemPingResult>(
            "health",
            ApplicationOperations.SystemPing,
            new { },
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { status = "ready", respondedAt = response.RespondedAt });
    }
}
