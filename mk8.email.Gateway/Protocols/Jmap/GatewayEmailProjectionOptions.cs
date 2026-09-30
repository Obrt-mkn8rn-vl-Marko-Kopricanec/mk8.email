namespace mk8.email.Gateway.Protocols.Jmap;

internal sealed record GatewayEmailProjectionOptions(
    IReadOnlyList<string> Properties,
    IReadOnlyList<string> BodyProperties,
    bool FetchTextBodyValues,
    bool FetchHtmlBodyValues,
    bool FetchAllBodyValues,
    int MaxBodyValueBytes);
