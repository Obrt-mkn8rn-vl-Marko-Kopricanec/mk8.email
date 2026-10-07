using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes these bounded FindItem parser/query/convergence vectors; discovery is retained.")]
internal sealed class GatewayEwsFindItemTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Folder = new("1338f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Item = new("2238f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly string[] ShapeProperties = ["ItemId", "Subject", "IsRead"];
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "fixture-secret");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 1024, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);
    private static readonly MailFolderSnapshot FolderSnapshot = new(Folder, "Inbox", null, "inbox", 0, true, 1, 1, 1, 1, true);

    [TestMethod]
    [DataRow("")]
    [DataRow("<m:IndexedPageItemView Offset='0' BasePoint='Beginning'/>")]
    [DataRow("<m:IndexedPageItemView MaxEntriesReturned='2' Offset='2147483647' BasePoint='Beginning'/>")]
    public async Task StrictShapesAndRepeatedParentsRetainBeginningIndexedPaging(string view)
    {
        var fields = "<t:AdditionalProperties><t:FieldURI FieldURI='item:Subject'/><t:FieldURI FieldURI='message:IsRead'/></t:AdditionalProperties>";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Request(view, fields: fields)));
        var request = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("FindItem", request.Operation, StringComparer.Ordinal);
        Assert.IsFalse(request.IsMutation);
        Assert.IsNull(request.Items);
        Assert.IsFalse(request.Deep);
        Assert.HasCount(2, request.Folders);
        CollectionAssert.AreEquivalent(ShapeProperties, request.Properties.ToArray());
        Assert.AreEqual(view.Length != 0, request.Indexed);
        Assert.AreEqual(view.Contains("2147483647", StringComparison.Ordinal) ? int.MaxValue : 0, request.Offset);
        Assert.AreEqual(view.Contains("MaxEntriesReturned", StringComparison.Ordinal) ? 2 : 32, request.Limit);
    }

    [TestMethod]
    [DataRow("Deep", "", "", "ErrorInvalidTraversal")]
    [DataRow("SoftDeleted", "", "", "ErrorInvalidTraversal")]
    [DataRow("Associated", "", "", "ErrorInvalidTraversal")]
    [DataRow("Shallow", "<m:FractionalPageItemView Numerator='0' Denominator='1'/>", "", "ErrorInvalidRequest")]
    [DataRow("Shallow", "<m:Restriction/>", "", "ErrorInvalidRequest")]
    [DataRow("Shallow", "<m:SortOrder/>", "", "ErrorInvalidRequest")]
    [DataRow("Shallow", "<m:IndexedPageItemView Offset='0' BasePoint='End'/>", "", "ErrorInvalidIndexedPagingParameters")]
    [DataRow("Shallow", "<m:IndexedPageItemView Offset='-1' BasePoint='Beginning'/>", "", "ErrorInvalidIndexedPagingParameters")]
    [DataRow("Shallow", "<m:IndexedPageItemView Offset='2147483648' BasePoint='Beginning'/>", "", "ErrorInvalidIndexedPagingParameters")]
    [DataRow("Shallow", "<m:IndexedPageItemView BasePoint='Beginning'/>", "", "ErrorInvalidIndexedPagingParameters")]
    [DataRow("Shallow", "<m:IndexedPageItemView MaxEntriesReturned='0' Offset='0' BasePoint='Beginning'/>", "", "ErrorInvalidIndexedPagingParameters")]
    [DataRow("Shallow", "<m:IndexedPageItemView MaxEntriesReturned='33' Offset='0' BasePoint='Beginning'/>", "", "ErrorExceededFindCountLimit")]
    [DataRow("Shallow", "<m:IndexedPageItemView Offset='0' BasePoint='Beginning'><t:ItemId Id='nested'/></m:IndexedPageItemView>", "", "ErrorSchemaValidation")]
    [DataRow("Shallow", "", "<t:IncludeMimeContent>true</t:IncludeMimeContent>", "ErrorInvalidPropertyRequest")]
    [DataRow("Shallow", "", "<t:AdditionalProperties><t:FieldURI FieldURI='message:From'/></t:AdditionalProperties>", "ErrorInvalidPropertyRequest")]
    [DataRow("Shallow", "", "<t:AdditionalProperties><t:FieldURI FieldURI='message:Sender'/></t:AdditionalProperties>", "ErrorInvalidPropertyRequest")]
    [DataRow("Shallow", "", "<t:AdditionalProperties><t:FieldURI FieldURI='message:ToRecipients'/></t:AdditionalProperties>", "ErrorInvalidPropertyRequest")]
    [DataRow("Shallow", "", "<t:AdditionalProperties><t:FieldURI FieldURI='message:InternetMessageId'/></t:AdditionalProperties>", "ErrorInvalidPropertyRequest")]
    public async Task UnsupportedAndInvalidFindItemShapesNeverBecomeQueries(string traversal, string view, string fields, string code)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Request(view, traversal, fields)));
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(code, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ParentCountIsBoundedAndFieldOrderCannotHideAdvancedSearch()
    {
        var xml = Request("").Replace("<t:DistinguishedFolderId Id='inbox'/><t:DistinguishedFolderId Id='inbox'/>",
            string.Concat(Enumerable.Repeat("<t:DistinguishedFolderId Id='inbox'/>", 33)), StringComparison.Ordinal);
        using var tooMany = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.AreEqual("ErrorExceededFindCountLimit", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(tooMany, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        using var reordered = new MemoryStream(Encoding.UTF8.GetBytes(Request("").Replace("</m:ParentFolderIds>", "</m:ParentFolderIds><m:QueryString>private</m:QueryString>", StringComparison.Ordinal)));
        Assert.AreEqual("ErrorSchemaValidation", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(reordered, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("duplicate")]
    [DataRow("empty-id")]
    [DataRow("missing-id")]
    [DataRow("position")]
    [DataRow("total")]
    [DataRow("state")]
    [DataRow("authorized")]
    [DataRow("denied-with-data")]
    [DataRow("extra")]
    [DataRow("wrong-operation")]
    [DataRow("known-entity")]
    public async Task QueryRepliesRequireExactWindowShapeAndDistinctNonemptyIds(string mode)
    {
        var value = new MailMessageQueryResult(MailMessageQueryStatus.Ok, "s10", 0, [Item], 1);
        var data = JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web)!.AsObject();
        if (mode is "duplicate") { data["ids"] = new JsonArray(Item, Item); data["total"] = 2; }
        if (mode is "empty-id") data["ids"] = new JsonArray(Guid.Empty);
        if (mode is "missing-id") data["ids"] = new JsonArray();
        if (mode is "position") data["position"] = 1;
        if (mode is "total") data["total"] = -1;
        if (mode is "state") data["state"] = null;
        if (mode is "authorized") data["status"] = (int)MailMessageQueryStatus.Authorized;
        if (mode is "denied-with-data") data["status"] = (int)MailMessageQueryStatus.AccountNotFound;
        if (mode is "extra") data["unexpected"] = true;
        var reply = Reply(mode is "wrong-operation" ? MailOperationKind.ReadMessages : MailOperationKind.FindMessages, data);
        if (mode is "known-entity") reply = reply with { OperationResult = reply.OperationResult! with { KnownEntities = new Dictionary<string, string>(StringComparer.Ordinal) { ["opaque"] = "unexpected" } } };
        var transport = new Transport([reply]);
        var client = new GatewayEwsClient(transport, new EnvironmentConfig());
        if (mode is "valid") Assert.AreEqual(Item, (await client.QueryItemsAsync(Authentication, Profile, Account, Folder, 0, 2, CancellationToken.None).ConfigureAwait(false)).Ids.Single());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryItemsAsync(Authentication, Profile, Account, Folder, 0, 2, CancellationToken.None)).ConfigureAwait(false);
        var command = transport.Commands.Single().Arguments.Deserialize<MailMessageQueryCommand>(JsonSerializerOptions.Web)!;
        Assert.AreEqual(Account, command.AccountId);
        Assert.IsFalse(command.CheckAccountOnly);
        Assert.IsFalse(command.Criteria.CollapseThreads);
        Assert.AreEqual($"M{Folder:N}", command.Criteria.Filter!.Terms!.Single().Text, StringComparer.Ordinal);
        Assert.AreEqual(MailMessageSortField.ReceivedAt, command.Criteria.Sort.Single().Field);
        Assert.IsFalse(command.Criteria.Sort.Single().IsAscending);
        Assert.IsNull(command.AnchorId);
    }

    [TestMethod]
    [DataRow("state")]
    [DataRow("missing")]
    [DataRow("folder")]
    [DataRow("parent-state")]
    public async Task ChangedStateOrMembershipNeverProducesAMixedFindPage(string mode)
    {
        var snapshot = Snapshot(mode is "folder" ? Account : Folder);
        var read = new MailMessageReadResult(MailMessageReadStatus.Ok, mode is "state" ? "s11" : "s10", mode is "missing" ? [] : [new(Item, snapshot)]);
        var transport = new Transport([Query(), Reply(MailOperationKind.ReadMessages, read), FolderQuery(mode is "parent-state" ? "f11" : "f10"), FolderRead(mode is "parent-state" ? "f11" : "f10")]);
        var client = new GatewayEwsClient(transport, new EnvironmentConfig());
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsFindItemResponse.ExecuteAsync(client, Authentication, Profile, Account,
            Parsed(), new(Account, "f10", [FolderSnapshot]), "ErrorFolderNotFound", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("ErrorServerBusy", error.Code, StringComparer.Ordinal);
        Assert.AreEqual(503, error.Status);
    }

    [TestMethod]
    public async Task DuplicateParentsReuseOneQueryAndReadButRetainTwoOrderedResults()
    {
        var transport = new Transport([Query(), Reply(MailOperationKind.ReadMessages, new MailMessageReadResult(MailMessageReadStatus.Ok, "s10", [new(Item, Snapshot(Folder))])), FolderQuery(), FolderRead("f10")]);
        var client = new GatewayEwsClient(transport, new EnvironmentConfig());
        var request = Parsed() with { Folders = [new("inbox", true, null), new("inbox", true, null)] };
        var xml = await GatewayEwsFindItemResponse.ExecuteAsync(client, Authentication, Profile, Account, request, new(Account, "f10", [FolderSnapshot]), "ErrorFolderNotFound", CancellationToken.None).ConfigureAwait(false);
        var roots = XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "RootFolder").ToArray();
        Assert.HasCount(2, roots);
        Assert.AreEqual(roots[0].ToString(), roots[1].ToString(), StringComparer.Ordinal);
        Assert.HasCount(4, transport.Commands);
        Assert.IsFalse(transport.Commands.Single(command => command.Operation == MailOperationKind.ReadMessages).Arguments["includeText"]!.GetValue<bool>());
        Assert.AreEqual("1", (string?)roots[0].Attribute("IndexedPagingOffset"), StringComparer.Ordinal);
        Assert.AreEqual("true", (string?)roots[0].Attribute("IncludesLastItemInRange"), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task LostAccountAndLowerObjectLimitAreExplicitAndNeverBroadenTheQuery()
    {
        var transport = new Transport([Reply(MailOperationKind.FindMessages, new MailMessageQueryResult(MailMessageQueryStatus.AccountNotFound, null, 0, [], 0))]);
        var client = new GatewayEwsClient(transport, new EnvironmentConfig());
        Assert.AreEqual(MailMessageQueryStatus.AccountNotFound, (await client.QueryItemsAsync(Authentication, Profile, Account, Folder, 0, 2, CancellationToken.None).ConfigureAwait(false)).Status);
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => client.QueryItemsAsync(Authentication,
            Profile with { Limits = Profile.Limits with { MaxObjectsInGet = 1 } }, Account, Folder, 0, 2, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("ErrorExceededFindCountLimit", error.Code, StringComparer.Ordinal);
        Assert.HasCount(1, transport.Commands);
    }

    private static GatewayEwsRequest Parsed() => new("FindItem", new HashSet<string>(["ItemId"], StringComparer.Ordinal), [new("inbox", true, null)], false, 0, 2, true);
    private static string Request(string view, string traversal = "Shallow", string fields = "") => $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:FindItem Traversal='{traversal}'><m:ItemShape><t:BaseShape>IdOnly</t:BaseShape>{fields}</m:ItemShape>{view}<m:ParentFolderIds><t:DistinguishedFolderId Id='inbox'/><t:DistinguishedFolderId Id='inbox'/></m:ParentFolderIds></m:FindItem></s:Body></s:Envelope>";
    private static MailMessageSnapshot Snapshot(Guid folder) => new(Item, null, null, 123, new(Item, folder, "thread", [], 123, new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc)), [], null, []);
    private static JmapApplicationResult Query() => Reply(MailOperationKind.FindMessages, new MailMessageQueryResult(MailMessageQueryStatus.Ok, "s10", 0, [Item], 1));
    private static JmapApplicationResult FolderQuery(string state = "f10") => Reply(MailOperationKind.FindFolders, new MailFolderQueryResult(MailFolderQueryStatus.Ok, state, 0, [], 1));
    private static JmapApplicationResult FolderRead(string state) => Reply(MailOperationKind.ReadFolders, new MailFolderReadResult(MailFolderReadStatus.Ok, state, [FolderSnapshot]));
    private static JmapApplicationResult Reply<T>(MailOperationKind kind, T value) => new(JmapApplicationOutcomes.Ok,
        OperationResult: new(new(kind, ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web))), new Dictionary<string, string>(StringComparer.Ordinal), Profile));

    private sealed class Transport(IReadOnlyList<JmapApplicationResult> replies) : IGatewayApplicationTransport
    {
        internal List<MailOperationCommand> Commands { get; } = [];
        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ews", protocol, StringComparer.Ordinal);
            Assert.AreEqual(ApplicationOperations.MailOperationExecute, operation, StringComparer.Ordinal);
            var input = request as MailOperationApplicationRequest;
            Assert.IsNotNull(input);
            Assert.AreEqual(Authentication, input.Authentication);
            Commands.Add(input.Command);
            return Task.FromResult((TResponse)(object)replies[Commands.Count - 1]);
        }
    }
}
