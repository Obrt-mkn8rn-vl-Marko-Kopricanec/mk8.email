using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.ApplicationBridge;

public static class GatewayApplicationHealth
{
    public static async Task<IResult> CheckAsync(
        IGatewayApplicationTransport transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var response = await transport.SendAsync<object, SystemPingResult>(
            "health",
            ApplicationOperations.SystemPing,
            new { },
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.ContractVersion, DistributedContractVersions.Current, StringComparison.Ordinal))
            return Results.Problem("The Application Worker uses incompatible distributed contracts.", statusCode: StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(new { status = "ready", respondedAt = response.RespondedAt });
    }
}
