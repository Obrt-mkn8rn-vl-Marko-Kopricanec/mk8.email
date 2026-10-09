using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

internal sealed partial class GatewayEwsItemCopyTests
{
    [TestMethod]
    [DataRow("inbox")]
    [DataRow("drafts")]
    [DataRow("sentitems")]
    [DataRow("deleteditems")]
    [DataRow("junkemail")]
    public async Task DistinguishedItemDestinationsAdmitOnlyNamedPhysicalMailRoles(string name)
    {
        using var body = Body($"<t:DistinguishedFolderId Id='{name}'><t:Mailbox><t:EmailAddress>OWNER@example.test</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>", "<t:ItemId Id='item'/>");
        var request = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(request.Folders[0].Distinguished);
        Assert.AreEqual(name, request.Folders[0].Id, StringComparer.Ordinal);
        Assert.AreEqual("OWNER@example.test", request.Folders[0].Mailbox, StringComparer.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow("msgfolderroot")]
    [DataRow("calendar")]
    [DataRow("contacts")]
    [DataRow("outbox")]
    [DataRow("archivedeleteditems")]
    public async Task DistinguishedItemDestinationsDoNotInventVirtualNonmailOrArchiveRoles(string name)
    {
        using var body = Body($"<t:DistinguishedFolderId Id='{name}'/>", "<t:ItemId Id='item'/>");
        Assert.AreEqual("ErrorInvalidRequest", (await Assert.ThrowsAsync<GatewayEwsRequestException>(
            () => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DistinguishedItemDestinationsCarryRequiredGraphAndEmailStatesIntoNativeCopyOrMove(bool moving)
    {
        var source = Sources()[Source];
        source = source with { Stored = source.Stored! with { FolderId = Guid.NewGuid() } };
        var result = moving ? MoveSuccess() : Success();
        var transport = new Transport([DestinationQuery(1), DestinationRead("drafts"),
            Reply(MailOperationKind.ReadMessages, new MailMessageReadResult(MailMessageReadStatus.Ok, "s10", [new(Source, source)])),
            new(JmapApplicationOutcomes.Ok, OperationResult: Operation(JsonSerializer.SerializeToNode(result, JsonSerializerOptions.Web)!,
                new Dictionary<string, string>(StringComparer.Ordinal) { [moving ? "ewsMove0" : "ewsCopy0"] = $"E{Created:N}" }))]);
        var request = Request([new(GatewayEwsItemIdCodec.Encode(Account, Source), null)]) with
        {
            Operation = moving ? "MoveItem" : "CopyItem",
            Folders = [new("drafts", true, "OWNER@example.test")],
        };
        var xml = await GatewayEwsItemCopy.ExecuteAsync(new(transport, new()), Authentication, Profile, Account, request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("NoError", XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        CollectionAssert.AreEqual(new[] { MailOperationKind.FindFolders, MailOperationKind.ReadFolders, MailOperationKind.ReadMessages, MailOperationKind.CopyMessages },
            transport.Commands.Select(command => command.Operation).ToArray());
        var native = transport.Commands[^1].Arguments.Deserialize<MailCopyCommand>(JsonSerializerOptions.Web)!;
        Assert.AreEqual("mailboxes10", native.IfMailboxInState, StringComparer.Ordinal);
        Assert.AreEqual("s10", native.IfInState, StringComparer.Ordinal);
        Assert.AreEqual("s10", native.IfFromInState, StringComparer.Ordinal);
        Assert.AreEqual(Target, native.Items[0].MailboxId);
        Assert.AreEqual(moving, native.DestroyOriginal);
    }

    [TestMethod]
    [DataRow("foreign@example.test")]
    [DataRow("alias@example.test")]
    public async Task DistinguishedItemDestinationsCannotGrantForeignOrAliasMailboxAuthority(string address)
    {
        var transport = new Transport([]);
        var request = Request([new(GatewayEwsItemIdCodec.Encode(Account, Source), null)]) with { Folders = [new("inbox", true, address)] };
        var xml = await GatewayEwsItemCopy.ExecuteAsync(new(transport, new()), Authentication, Profile, Account, request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("ErrorAccessDenied", XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(transport.Commands);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DistinguishedItemDestinationsRequireOneUnambiguousCurrentRole(bool ambiguous)
    {
        var folder = new MailFolderSnapshot(Target, "Drafts", null, ambiguous ? "drafts" : null, 0, true, 0, 0, 0, 0, true);
        MailFolderSnapshot[] folders = ambiguous ? [folder, folder with { Id = Created }] : [folder];
        var transport = new Transport([DestinationQuery(folders.Length), Reply(MailOperationKind.ReadFolders,
            new MailFolderReadResult(MailFolderReadStatus.Ok, "mailboxes10", folders))]);
        var request = Request([new(GatewayEwsItemIdCodec.Encode(Account, Source), null)]) with { Folders = [new("drafts", true, null)] };
        var xml = await GatewayEwsItemCopy.ExecuteAsync(new(transport, new()), Authentication, Profile, Account, request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("ErrorFolderNotFound", XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.HasCount(2, transport.Commands);
    }

    [TestMethod]
    public async Task DistinguishedItemDestinationsEnforceObjectLimitsBeforeGraphTransport()
    {
        var transport = new Transport([]);
        var request = Request([new(GatewayEwsItemIdCodec.Encode(Account, Source), null)]) with { Folders = [new("drafts", true, null)] };
        var profile = Profile with { Limits = Profile.Limits with { MaxObjectsInSet = 0 } };
        Assert.AreEqual("ErrorExceededFindCountLimit", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() =>
            GatewayEwsItemCopy.ExecuteAsync(new(transport, new()), Authentication, profile, Account, request, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        Assert.IsEmpty(transport.Commands);
    }

    private static JmapApplicationResult DestinationQuery(int count) => Reply(MailOperationKind.FindFolders,
        new MailFolderQueryResult(MailFolderQueryStatus.Ok, "mailboxes10", 0, [], count));
    private static JmapApplicationResult DestinationRead(string role) => Reply(MailOperationKind.ReadFolders,
        new MailFolderReadResult(MailFolderReadStatus.Ok, "mailboxes10", [new(Target, "Destination", null, role, 0, true, 0, 0, 0, 0, true)]));
}
