namespace mk8.email.Gateway.Protocols.Jmap;

internal sealed record GatewayJmapProblem(string Type, string Title, string? Detail = null, string? Limit = null);
