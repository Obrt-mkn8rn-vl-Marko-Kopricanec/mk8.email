using System.Text.Json;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

internal sealed partial class GatewayEwsItemCreateTests
{
    [TestMethod]
    [DataRow("inbox")]
    [DataRow("drafts")]
    [DataRow("sentitems")]
    [DataRow("deleteditems")]
    [DataRow("junkemail")]
    public async Task DistinguishedCreateDestinationsPreserveNativeMimeAndRegisteredMailbox(string name)
    {
        using var body = Body(Message(Convert.ToBase64String(Native)), $"<t:DistinguishedFolderId Id='{name}'><t:Mailbox><t:EmailAddress>owner@example.test</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>");
        var request = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(request.Folders[0].Distinguished);
        Assert.AreEqual(name, request.Folders[0].Id, StringComparer.Ordinal);
        CollectionAssert.AreEqual(Native, request.MimeCreates![0]);
    }

    [TestMethod]
    public async Task DistinguishedCreateDestinationsCarryRequiredGraphAndEmailStateIntoImport()
    {
        var query = new MailFolderQueryResult(MailFolderQueryStatus.Ok, "mailboxes10", 0, [], 1);
        var read = new MailFolderReadResult(MailFolderReadStatus.Ok, "mailboxes10", [new(Target, "Drafts", null, "drafts", 0, true, 0, 0, 0, 0, true)]);
        var transport = new Transport([new(JmapApplicationOutcomes.Ok, OperationResult: Operation(MailOperationKind.FindFolders, JsonSerializer.SerializeToNode(query, JsonSerializerOptions.Web)!)),
            new(JmapApplicationOutcomes.Ok, OperationResult: Operation(MailOperationKind.ReadFolders, JsonSerializer.SerializeToNode(read, JsonSerializerOptions.Web)!)),
            State(), Upload(), Result(Success() with { Items = [Success().Items[0]] })]);
        var request = Request([Native]) with { Folders = [new("drafts", true, null)] };
        var xml = await GatewayEwsItemCreate.ExecuteAsync(new(transport, new()), Authentication, Profile, Account, request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("NoError", XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        var import = transport.Commands[^1].Arguments.Deserialize<MailImportCommand>(JsonSerializerOptions.Web)!;
        Assert.AreEqual("mailboxes10", import.IfMailboxInState, StringComparer.Ordinal);
        Assert.AreEqual("s10", import.IfInState, StringComparer.Ordinal);
        Assert.AreEqual(Target, import.Items[0].MailboxId);
        CollectionAssert.AreEqual(DraftKeywords, import.Items[0].Keywords.ToArray());
        Assert.HasCount(1, transport.Uploads);
    }

    [TestMethod]
    [DataRow("foreign@example.test")]
    [DataRow("alias@example.test")]
    public async Task DistinguishedCreateDestinationsCannotUseAnotherMailboxOrUploadBeforeAdmission(string address)
    {
        var transport = new Transport([]);
        var request = Request([Native]) with { Folders = [new("drafts", true, address)] };
        var xml = await GatewayEwsItemCreate.ExecuteAsync(new(transport, new()), Authentication, Profile, Account, request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("ErrorAccessDenied", XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(transport.Commands);
        Assert.IsEmpty(transport.Uploads);
    }

    [TestMethod]
    public async Task DistinguishedCreateDestinationsEnforceMimeBudgetBeforeGraphOrUpload()
    {
        var transport = new Transport([]);
        var request = Request([Native]) with { Folders = [new("drafts", true, null)] };
        var profile = Profile with { Limits = Profile.Limits with { MaxUploadSizeBytes = Native.Length - 1 } };
        var xml = await GatewayEwsItemCreate.ExecuteAsync(new(transport, new()), Authentication, profile, Account, request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("ErrorDataSizeLimitExceeded", XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(transport.Commands);
        Assert.IsEmpty(transport.Uploads);
    }
}
