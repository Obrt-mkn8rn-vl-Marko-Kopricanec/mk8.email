namespace mk8.email.Gateway.Protocols.Ews;

internal sealed record GatewayEwsItemCreatePlan(string Token, byte[] Content, string? Code);
