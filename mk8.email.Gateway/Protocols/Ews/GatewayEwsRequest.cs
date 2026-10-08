namespace mk8.email.Gateway.Protocols.Ews;

internal sealed record GatewayEwsRequest(
    string Operation,
    IReadOnlySet<string> Properties,
    IReadOnlyList<GatewayEwsFolderReference> Folders,
    bool Deep,
    int Offset,
    int Limit,
    bool Indexed,
    IReadOnlyList<string>? Names = null,
    IReadOnlyList<GatewayEwsItemReference>? Items = null,
    string BodyType = "Best",
    IReadOnlyList<string>? Attachments = null,
    bool ReturnNewItemIds = true,
    IReadOnlyList<bool>? ReadStates = null,
    IReadOnlyList<byte[]>? MimeCreates = null,
    string? SyncState = null,
    bool SyncScopeSpecified = false)
{
    public bool IsMutation => Operation is "CreateFolder" or "UpdateFolder" or "DeleteFolder" or "DeleteItem" or "CopyItem" or "MoveItem" or "UpdateItem" or "CreateItem";
}
