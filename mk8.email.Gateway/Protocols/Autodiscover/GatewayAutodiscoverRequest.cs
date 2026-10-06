namespace mk8.email.Gateway.Protocols.Autodiscover;

internal sealed record GatewayAutodiscoverRequest(string? EmailAddress, int ErrorCode);
