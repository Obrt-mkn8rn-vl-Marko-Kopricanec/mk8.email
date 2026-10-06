namespace mk8.email.Gateway.Protocols.Ews;

internal sealed record GatewayEwsRequest(
    string Operation,
    IReadOnlySet<string> Properties,
    IReadOnlyList<GatewayEwsFolderReference> Folders,
    bool Deep,
    int Offset,
    int Limit,
    bool Indexed);
