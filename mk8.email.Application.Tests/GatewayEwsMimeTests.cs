using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Ews;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this MIME admission and typed-content fixture; discovery is retained.")]
internal sealed class GatewayEwsMimeTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Item = new("2238f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Other = new("2338f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 2_097_152, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "fixture-secret");
    private const string MimeOption = "<t:IncludeMimeContent>true</t:IncludeMimeContent>";
    private static readonly byte[] Raw = "Subject: Raw MIME\r\nMIME-Version: 1.0\r\nContent-Type: text/plain\r\n\r\nExact <&> bytes\r\n"u8.ToArray();
    private static readonly string[] MimeFields = ["MimeContent", "ItemId"];

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("1", true)]
    [DataRow("false", false)]
    [DataRow("0", false)]
    public async Task IncludeMimeContentIsExplicitAndIndependentOfBody(string value, bool expected)
    {
        var request = await ParseAsync($"<t:IncludeMimeContent>{value}</t:IncludeMimeContent>").ConfigureAwait(false);
        Assert.AreEqual(expected, request.Properties.Contains("MimeContent"));
        Assert.IsFalse(request.Properties.Contains("Body"));
    }

    [TestMethod]
    [DataRow("<t:IncludeMimeContent>True</t:IncludeMimeContent>", false)]
    [DataRow("<t:IncludeMimeContent>true</t:IncludeMimeContent><t:IncludeMimeContent>false</t:IncludeMimeContent>", false)]
    [DataRow("<t:IncludeMimeContent><t:Value>true</t:Value></t:IncludeMimeContent>", false)]
    [DataRow("<t:AdditionalProperties><t:FieldURI FieldURI='item:MimeContent'/></t:AdditionalProperties>", false)]
    [DataRow(MimeOption, true)]
    public async Task InvalidMimeShapeAndFindItemDoNotAcquireContent(string fields, bool find)
    {
        await Assert.ThrowsAsync<GatewayEwsRequestException>(() => ParseAsync(fields, find)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MimeTransportIsBoundedAndMetadataBodySelectionIsIndependent()
    {
        var transport = new Transport(Reply(new(MailMessageReadStatus.Ok, "s1", [Content()])));
        var request = await ParseAsync(MimeOption).ConfigureAwait(false);
        var client = new GatewayEwsClient(transport, new EnvironmentConfig());
        var xml = await GatewayEwsItemResponse.ExecuteAsync(client, Authentication, Profile, Account, request, CancellationToken.None).ConfigureAwait(false);
        var message = XDocument.Parse(xml).Descendants(GatewayEwsSoap.Types + "Message").Single();
        CollectionAssert.AreEqual(MimeFields, message.Elements().Select(element => element.Name.LocalName).ToArray());
        CollectionAssert.AreEqual(Raw, Convert.FromBase64String(message.Element(GatewayEwsSoap.Types + "MimeContent")!.Value));
        Assert.AreEqual("us-ascii", (string?)message.Element(GatewayEwsSoap.Types + "MimeContent")!.Attribute("CharacterSet"), StringComparer.Ordinal);
        Assert.HasCount(1, transport.Commands);
        Assert.IsFalse(transport.Commands[0].IncludeText);
        Assert.AreEqual(GatewayEwsClient.MaximumMimeBytes, transport.Commands[0].MaximumBytes);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("state")]
    [DataRow("duplicate")]
    [DataRow("missing")]
    [DataRow("unexpected")]
    [DataRow("stored")]
    [DataRow("source")]
    [DataRow("size")]
    [DataRow("raw-size")]
    [DataRow("empty")]
    [DataRow("over-limit")]
    [DataRow("failure-with-data")]
    [DataRow("error-with-content")]
    [DataRow("error-with-snapshot")]
    [DataRow("unknown-status")]
    [DataRow("known-entity")]
    public async Task NativeRepliesRequireExactCorrelatedShape(string mode)
    {
        var item = Content();
        if (mode is "unexpected") item = item with { MessageId = Other };
        if (mode is "stored") item = item with { Value = item.Value! with { Stored = null } };
        if (mode is "source") item = item with { Value = item.Value! with { ContentSourceId = Other } };
        if (mode is "size") item = item with { Value = item.Value! with { Stored = item.Value.Stored! with { Size = Raw.Length + 1 } } };
        if (mode is "raw-size") item = item with { Value = item.Value! with { RawSize = Raw.Length + 1 } };
        if (mode is "empty") item = item with { Content = default };
        if (mode is "over-limit") item = item with { Content = new byte[GatewayEwsClient.MaximumMimeBytes + 1] };
        if (mode is "error-with-content") item = item with { Status = MailMessageContentStatus.NotFound, Value = null };
        if (mode is "error-with-snapshot") item = item with { Status = MailMessageContentStatus.NotFound, Content = default };
        if (mode is "unknown-status") item = item with { Status = (MailMessageContentStatus)99 };
        var read = new MailMessageContentResult(mode is "failure-with-data" ? MailMessageReadStatus.AccountNotFound : MailMessageReadStatus.Ok,
            mode is "state" ? null : "s1", mode is "missing" ? [] : mode is "duplicate" ? [item, item] : [item]);
        var result = Reply(read);
        if (mode is "known-entity") result = result with { OperationResult = result.OperationResult! with { KnownEntities = new Dictionary<string, string>(StringComparer.Ordinal) { ["unexpected"] = "value" } } };
        var client = new GatewayEwsClient(new Transport(result), new EnvironmentConfig());
        if (mode is "valid") Assert.HasCount(1, (await client.ReadItemContentAsync(Authentication, Profile, Account, [Item], false, CancellationToken.None).ConfigureAwait(false)).Messages);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadItemContentAsync(Authentication, Profile, Account, [Item], false, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(MailMessageContentStatus.NotFound, "ErrorItemNotFound")]
    [DataRow(MailMessageContentStatus.TooLarge, "ErrorDataSizeLimitExceeded")]
    [DataRow(MailMessageContentStatus.NotParsable, "ErrorInvalidPropertyRequest")]
    public async Task NativeFailuresAreOrderedPerReference(MailMessageContentStatus status, string code)
    {
        var read = new MailMessageContentResult(MailMessageReadStatus.Ok, "s1", [new(Item, status, null, default)]);
        var request = await ParseAsync(MimeOption).ConfigureAwait(false);
        var xml = await GatewayEwsItemResponse.ExecuteAsync(new(new Transport(Reply(read)), new EnvironmentConfig()), Authentication,
            Profile, Account, request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(code, XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(GatewayEwsSoap.Types + "MimeContent"));
    }

    [TestMethod]
    public void EightBitMimeIsExplicitlyRefusedWithoutTranscoding()
    {
        var raw = Encoding.UTF8.GetBytes("Subject: č\r\n\r\nbody");
        var value = Snapshot(raw.Length);
        var error = Assert.Throws<GatewayEwsRequestException>(() => GatewayEwsItemResponse.Message(Account, "s1", value,
            new HashSet<string>(["ItemId", "MimeContent"], StringComparer.Ordinal), mimeContent: raw));
        Assert.AreEqual("ErrorInvalidPropertyRequest", error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("extra")]
    [DataRow("null-ids")]
    [DataRow("duplicate")]
    [DataRow("zero")]
    [DataRow("too-large")]
    public async Task WorkerRefusesInvalidNativeCommandsBeforeReading(string mode)
    {
        var fixture = await JmapFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var arguments = JsonSerializer.SerializeToNode(new MailMessageContentCommand(fixture.InboxId, [Item], false, 1024), JsonSerializerOptions.Web)!.AsObject();
        if (mode is "extra") arguments["projection"] = true;
        if (mode is "null-ids") arguments["messageIds"] = null;
        if (mode is "duplicate") arguments["messageIds"] = new JsonArray(Item.ToString(), Item.ToString());
        if (mode is "zero") arguments["maximumBytes"] = 0;
        if (mode is "too-large") arguments["maximumBytes"] = int.MaxValue;
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages], MailOperationKind.ReadMessageContent, arguments, new Dictionary<string, string>(StringComparer.Ordinal));
        await Assert.ThrowsAsync<MailApplicationException>(() => scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>().ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
    }

    private static Task<GatewayEwsRequest> ParseAsync(string fields, bool find = false)
    {
        var suffix = find ? "<m:ParentFolderIds><t:DistinguishedFolderId Id='inbox'/></m:ParentFolderIds>" : $"<m:ItemIds><t:ItemId Id='{GatewayEwsItemIdCodec.Encode(Account, Item)}'/></m:ItemIds>";
        var operation = find ? "FindItem Traversal='Shallow'" : "GetItem";
        var end = find ? "FindItem" : "GetItem";
        var xml = $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:{operation}><m:ItemShape><t:BaseShape>IdOnly</t:BaseShape>{fields}</m:ItemShape>{suffix}</m:{end}></s:Body></s:Envelope>";
        return ReadAsync(xml);
    }

    private static async Task<GatewayEwsRequest> ReadAsync(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false);
    }

    private static MailMessageSnapshot Snapshot(int size) => new(Item, null, null, size,
        new(Item, Other, "thread", [], size, new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc)), [], null, []);
    private static MailMessageContentItem Content() => new(Item, MailMessageContentStatus.Ok, Snapshot(Raw.Length), Raw);
    private static JmapApplicationResult Reply(MailMessageContentResult read) => new(JmapApplicationOutcomes.Ok,
        OperationResult: new(new(MailOperationKind.ReadMessageContent, ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(read, JsonSerializerOptions.Web))),
            new Dictionary<string, string>(StringComparer.Ordinal), Profile));

    private sealed class Transport(JmapApplicationResult result) : IGatewayApplicationTransport
    {
        internal List<MailMessageContentCommand> Commands { get; } = [];
        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ews", protocol, StringComparer.Ordinal);
            Assert.AreEqual(ApplicationOperations.MailOperationExecute, operation, StringComparer.Ordinal);
            var input = request as MailOperationApplicationRequest;
            Assert.IsNotNull(input);
            Assert.AreEqual(Authentication, input.Authentication);
            Assert.AreEqual(MailOperationKind.ReadMessageContent, input.Command.Operation);
            Commands.Add(input.Command.Arguments.Deserialize<MailMessageContentCommand>(JsonSerializerOptions.Web)!);
            return Task.FromResult((TResponse)(object)result);
        }
    }
}
