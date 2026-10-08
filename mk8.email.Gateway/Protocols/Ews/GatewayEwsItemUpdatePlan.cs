namespace mk8.email.Gateway.Protocols.Ews;

internal sealed record GatewayEwsItemUpdatePlan(Guid Id, bool Read, string? Code);
