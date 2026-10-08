using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes these item synchronization/parser/token/reply vectors; discovered outcomes are retained.")]
internal sealed class GatewayEwsItemSyncTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Folder = new("1338f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Item = new("1438f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Other = new("1538f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal) { "ItemId", "IsRead" };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow(1, "")]
    [DataRow(512, "<m:SyncScope>NormalItems</m:SyncScope>")]
    public async Task ItemSyncRegisteredOrderLimitsAndOrdinaryScopeAreNotMutationAuthority(int maximum, string scope)
    {
        using var body = Body($"<m:MaxChangesReturned>{maximum}</m:MaxChangesReturned>{scope}");
        var request = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("SyncFolderItems", request.Operation, StringComparer.Ordinal);
        Assert.AreEqual(maximum, request.Limit);
        Assert.IsFalse(request.IsMutation);
        Assert.IsNull(request.Items);
        Assert.IsNull(request.SyncState);
        Assert.HasCount(1, request.Folders);
    }

    [TestMethod]
    [DataRow("", "ErrorSchemaValidation")]
    [DataRow("<m:MaxChangesReturned>0</m:MaxChangesReturned>", "ErrorSchemaValidation")]
    [DataRow("<m:MaxChangesReturned>513</m:MaxChangesReturned>", "ErrorSchemaValidation")]
    [DataRow("<m:MaxChangesReturned>1.0</m:MaxChangesReturned>", "ErrorSchemaValidation")]
    [DataRow("<m:MaxChangesReturned>-1</m:MaxChangesReturned>", "ErrorSchemaValidation")]
    [DataRow("<m:MaxChangesReturned>1</m:MaxChangesReturned><m:MaxChangesReturned>1</m:MaxChangesReturned>", "ErrorSchemaValidation")]
    [DataRow("<m:MaxChangesReturned>1</m:MaxChangesReturned><m:SyncScope>NormalAndAssociatedItems</m:SyncScope>", "ErrorInvalidRequest")]
    [DataRow("<m:Ignore/><m:MaxChangesReturned>1</m:MaxChangesReturned>", "ErrorInvalidRequest")]
    [DataRow("<m:SyncState>!</m:SyncState><m:MaxChangesReturned>1</m:MaxChangesReturned>", "ErrorInvalidSyncStateData")]
    [DataRow("<m:SyncState/><m:MaxChangesReturned>1</m:MaxChangesReturned>", "ErrorInvalidSyncStateData")]
    [DataRow("<m:MaxChangesReturned><?forbidden data?>1</m:MaxChangesReturned>", "ErrorSchemaValidation")]
    public async Task ItemSyncMalformedUnsupportedOptionalFieldsFailBeforeTransport(string fields, string code)
    {
        using var body = Body(fields);
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(code, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("<t:BaseShape>Default</t:BaseShape>")]
    [DataRow("<t:BaseShape>AllProperties</t:BaseShape>")]
    [DataRow("<t:BaseShape>IdOnly</t:BaseShape><t:IncludeMimeContent>true</t:IncludeMimeContent>")]
    [DataRow("<t:BaseShape>IdOnly</t:BaseShape><t:AdditionalProperties><t:FieldURI FieldURI='item:Body'/></t:AdditionalProperties>")]
    [DataRow("<t:BaseShape>IdOnly</t:BaseShape><t:AdditionalProperties><t:FieldURI FieldURI='item:Attachments'/></t:AdditionalProperties>")]
    [DataRow("<t:BaseShape>IdOnly</t:BaseShape><t:AdditionalProperties><t:FieldURI FieldURI='message:ToRecipients'/></t:AdditionalProperties>")]
    public async Task ItemSyncCannotAdmitBodyMimeOrUnsupportedShapes(string shape)
    {
        using var body = Body("<m:MaxChangesReturned>32</m:MaxChangesReturned>", shape);
        Assert.AreEqual("ErrorInvalidPropertyRequest", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public void ItemSyncMaximumCursorIsBoundedAndSortedWithoutGrantingAuthority()
    {
        var rows = Enumerable.Range(1, 32).ToDictionary(index => new Guid(index, 0, 0, new byte[8]), index => index % 2 == 0);
        var token = GatewayEwsItemSyncState.Encode(Account, Folder, "s10", GatewayEwsItemSyncState.Shape(Properties), rows);
        Assert.AreEqual(GatewayEwsItemSyncState.MaximumEncodedLength, token.Length);
        Assert.AreEqual(836, token.Length);
        Assert.IsTrue(GatewayEwsItemSyncState.TryDecode(token, out var cursor));
        Assert.HasCount(32, cursor!.ReadStates);
        rows.Add(new Guid(33, 0, 0, new byte[8]), false);
        Assert.Throws<InvalidOperationException>(() => GatewayEwsItemSyncState.Encode(Account, Folder, "s10", GatewayEwsItemSyncState.Shape(Properties), rows));
    }

    [TestMethod]
    [DataRow("s0")]
    [DataRow("s11")]
    [DataRow("s9223372036854775807")]
    public void ItemSyncCursorBindsCanonicalAccountFolderWatermarkShapeAndReadStates(string state)
    {
        var shape = GatewayEwsItemSyncState.Shape(Properties);
        var token = GatewayEwsItemSyncState.Encode(Account, Folder, state, shape, new Dictionary<Guid, bool> { [Item] = false, [Other] = true });
        Assert.IsTrue(GatewayEwsItemSyncState.TryDecode(token, out var cursor));
        Assert.AreEqual(Account, cursor!.Account);
        Assert.AreEqual(Folder, cursor.Folder);
        Assert.AreEqual(state, cursor.State, StringComparer.Ordinal);
        CollectionAssert.AreEqual(shape, cursor.Shape);
        Assert.IsFalse(cursor.ReadStates[Item]);
        Assert.IsTrue(cursor.ReadStates[Other]);
        Assert.AreEqual(token, GatewayEwsItemSyncState.Encode(cursor.Account, cursor.Folder, cursor.State, cursor.Shape, cursor.ReadStates), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("whitespace")]
    [DataRow("version")]
    [DataRow("zero-account")]
    [DataRow("zero-folder")]
    [DataRow("negative")]
    [DataRow("large-count")]
    [DataRow("count")]
    [DataRow("zero-id")]
    [DataRow("duplicate")]
    [DataRow("order")]
    [DataRow("flag")]
    [DataRow("truncated")]
    [DataRow("trailing")]
    public void ItemSyncCursorRejectsMalformedNoncanonicalOrUnboundedCacheData(string mode)
    {
        var token = GatewayEwsItemSyncState.Encode(Account, Folder, "s10", GatewayEwsItemSyncState.Shape(Properties), new Dictionary<Guid, bool> { [Item] = false, [Other] = true });
        var bytes = Convert.FromBase64String(token);
        if (mode is "empty") token = "";
        else if (mode is "whitespace") token = " " + token;
        else
        {
            if (mode is "version") bytes[7]++;
            if (mode is "zero-account") bytes.AsSpan(8, 16).Clear();
            if (mode is "zero-folder") bytes.AsSpan(24, 16).Clear();
            if (mode is "negative") bytes[40] = 0x80;
            if (mode is "large-count") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(80, 2), 33);
            if (mode is "count") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(80, 2), 1);
            if (mode is "zero-id") bytes.AsSpan(82, 16).Clear();
            if (mode is "duplicate") bytes.AsSpan(82, 16).CopyTo(bytes.AsSpan(99, 16));
            if (mode is "order")
            {
                var first = bytes.AsSpan(82, 17).ToArray();
                bytes.AsSpan(99, 17).CopyTo(bytes.AsSpan(82, 17));
                first.CopyTo(bytes, 99);
            }
            if (mode is "flag") bytes[98] = 2;
            if (mode is "truncated") bytes = bytes[..^1];
            if (mode is "trailing") bytes = [.. bytes, 0];
            token = Convert.ToBase64String(bytes);
        }
        Assert.IsFalse(GatewayEwsItemSyncState.TryDecode(token, out _));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("extra")]
    [DataRow("old-state")]
    [DataRow("future-to-past")]
    [DataRow("unchanged")]
    [DataRow("duplicate")]
    [DataRow("folder-key")]
    [DataRow("uppercase")]
    [DataRow("zero-id")]
    [DataRow("maximum")]
    [DataRow("unknown-status")]
    [DataRow("null-keys")]
    public void ItemSyncTypedChangesRefuseAmbiguousUncorrelatedOrOversizedReplies(string mode)
    {
        var data = JsonSerializer.SerializeToNode(new MailChangesResult(MailChangesStatus.Ok, "s10", "s11", false, [$"E{Item:N}"], [], []), Json)!.AsObject();
        if (mode is "missing") data.Remove("status");
        if (mode is "extra") data["extra"] = true;
        if (mode is "old-state") data["oldState"] = "s9";
        if (mode is "future-to-past") data["newState"] = "s9";
        if (mode is "unchanged") data["newState"] = "s10";
        if (mode is "duplicate") data["updatedKeys"] = new JsonArray($"E{Item:N}");
        if (mode is "folder-key") data["createdKeys"] = new JsonArray($"M{Item:N}");
        if (mode is "uppercase") data["createdKeys"] = new JsonArray($"E{Item:N}".ToUpperInvariant());
        if (mode is "zero-id") data["createdKeys"] = new JsonArray($"E{Guid.Empty:N}");
        if (mode is "maximum") data["updatedKeys"] = new JsonArray($"E{Other:N}");
        if (mode is "unknown-status") data["status"] = 500;
        if (mode is "null-keys") data["destroyedKeys"] = null;
        Assert.Throws<InvalidOperationException>(() => GatewayEwsItemSyncReply.Decode(data, "s10", 1));
    }

    [TestMethod]
    public void ItemSyncCompleteDeltaSupportsMoveInMoveOutReadFlagAndCreateDeleteCacheConvergence()
    {
        var prior = new GatewayEwsItemSyncState.Cursor(Account, Folder, "s10", GatewayEwsItemSyncState.Shape(Properties), new Dictionary<Guid, bool> { [Item] = false });
        var current = new Dictionary<Guid, bool> { [Other] = false };
        var moved = GatewayEwsItemSync.Project(Account, "s11", prior, [Projected(Other)], current,
            new(MailChangesStatus.Ok, "s10", "s11", false, [], [$"E{Item:N}", $"E{Other:N}"], []), Properties);
        Assert.IsNull(moved.Error);
        Assert.HasCount(1, moved.Changes!.Elements(GatewayEwsSoap.Types + "Create"));
        Assert.HasCount(1, moved.Changes.Elements(GatewayEwsSoap.Types + "Delete"));
        var read = GatewayEwsItemSync.Project(Account, "s11", prior, [Projected(Item, true)], new Dictionary<Guid, bool> { [Item] = true },
            new(MailChangesStatus.Ok, "s10", "s11", false, [], [$"E{Item:N}"], []), Properties);
        Assert.IsNull(read.Error);
        Assert.HasCount(1, read.Changes!.Elements(GatewayEwsSoap.Types + "Update"));
        Assert.HasCount(1, read.Changes.Elements(GatewayEwsSoap.Types + "ReadFlagChange"));
    }

    [TestMethod]
    [DataRow("missing-entry", "ErrorInvalidSyncStateData")]
    [DataRow("missing-exit", "ErrorInvalidSyncStateData")]
    [DataRow("missing-read", "ErrorInvalidSyncStateData")]
    [DataRow("partial", "ErrorExceededFindCountLimit")]
    [DataRow("state-race", "ErrorServerBusy")]
    public void ItemSyncUnverifiableMembershipReadFlagOrPartialPagesCannotAdvanceCache(string mode, string code)
    {
        var prior = new GatewayEwsItemSyncState.Cursor(Account, Folder, "s10", GatewayEwsItemSyncState.Shape(Properties), new Dictionary<Guid, bool> { [Item] = false });
        var id = mode is "missing-entry" ? Other : Item;
        IReadOnlyList<MailMessageProjectedItem> items = mode is "missing-exit" ? [] : [Projected(id, mode is "missing-read")];
        var current = items.ToDictionary(item => item.MessageId, item => item.Value.Stored!.Keywords.Contains("$seen", StringComparer.Ordinal));
        var delta = new MailChangesResult(MailChangesStatus.Ok, "s10", mode is "state-race" ? "s12" : "s11", mode is "partial", [], [], []);
        var result = GatewayEwsItemSync.Project(Account, "s11", prior, items, current, delta, Properties);
        Assert.AreEqual(code, result.Error, StringComparer.Ordinal);
        Assert.IsNull(result.Changes);
    }

    private static MailMessageProjectedItem Projected(Guid id, bool seen = false) => new(id,
        new(id, null, null, 0, new(id, Folder, "thread", seen ? ["$seen"] : [], 0, DateTime.UnixEpoch), [], null, []));
    private static MemoryStream Body(string fields, string shape = "<t:BaseShape>IdOnly</t:BaseShape>") => new(Encoding.UTF8.GetBytes(
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:SyncFolderItems><m:ItemShape>{shape}</m:ItemShape><m:SyncFolderId><t:FolderId Id='{GatewayEwsFolderIdCodec.Encode(Account, Folder)}'/></m:SyncFolderId>{fields}</m:SyncFolderItems></s:Body></s:Envelope>"));
}
