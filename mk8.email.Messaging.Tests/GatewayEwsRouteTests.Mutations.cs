using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;
using Npgsql;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private static readonly string[] CreateOutcomes = ["ErrorFolderExists", "NoError", "ErrorFolderExists"];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EwsCreateRenameAndHardDeleteConvergeWithJmap(bool disabledJmap)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true, disableJmap: disabledJmap).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var create = await WriteAsync(fixture, "CreateFolder", CreateFields("<t:DistinguishedFolderId Id='inbox'/>", "Cafe\u0301 &amp; test")).ConfigureAwait(false);
        AssertCode(create, "NoError");
        var identity = create.Descendants(Types + "FolderId").Single();
        var id = (string)identity.Attribute("Id")!;
        var key = (string)identity.Attribute("ChangeKey")!;
        Assert.IsTrue(GatewayEwsFolderIdCodec.TryDecode(id, out var account, out var folder));
        Assert.AreEqual(GatewayEwsFixtureDomain.AccountId, account);
        await AssertFolderAsync(fixture, id, "Café & test", key).ConfigureAwait(false);
        if (!disabledJmap) await AssertJmapFolderAsync(fixture, folder, "Café & test").ConfigureAwait(false);
        var update = await WriteAsync(fixture, "UpdateFolder", UpdateFields(id, "Renamed &lt;&amp;&gt;", key)).ConfigureAwait(false);
        AssertCode(update, "NoError");
        Assert.AreEqual(id, (string?)update.Descendants(Types + "FolderId").Single().Attribute("Id"), StringComparer.Ordinal);
        var nextKey = (string)update.Descendants(Types + "FolderId").Single().Attribute("ChangeKey")!;
        Assert.AreNotEqual(key, nextKey, StringComparer.Ordinal);
        await AssertFolderAsync(fixture, id, "Renamed <&>", nextKey).ConfigureAwait(false);
        if (!disabledJmap) await AssertJmapFolderAsync(fixture, folder, "Renamed <&>").ConfigureAwait(false);
        var delete = await WriteAsync(fixture, "DeleteFolder", $"<m:FolderIds><t:FolderId Id='{id}' ChangeKey='{nextKey}'/></m:FolderIds>").ConfigureAwait(false);
        AssertCode(delete, "NoError");
        Assert.IsFalse(delete.Descendants(Types + "Folders").Any());
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.IsFalse(await database.Folders.AnyAsync(item => item.Id == folder).ConfigureAwait(false));
        if (!disabledJmap) await AssertJmapFolderAsync(fixture, folder, null).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MixedCreateOutcomesPreserveOrderAndSiblingUniqueness()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var fields = "<m:ParentFolderId><t:DistinguishedFolderId Id='msgfolderroot'/></m:ParentFolderId><m:Folders>"
            + "<t:Folder><t:DisplayName>INBOX</t:DisplayName></t:Folder><t:Folder><t:FolderClass>IPF.Note</t:FolderClass><t:DisplayName>Fresh</t:DisplayName></t:Folder>"
            + "<t:Folder><t:DisplayName>fresh</t:DisplayName></t:Folder></m:Folders>";
        var reply = await WriteAsync(fixture, "CreateFolder", fields).ConfigureAwait(false);
        CollectionAssert.AreEqual(CreateOutcomes,
            reply.Descendants(Messages + "ResponseCode").Select(item => item.Value).ToArray());
        Assert.HasCount(1, reply.Descendants(Types + "FolderId"));
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.MailOperationExecute, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task HardDeleteOrdinaryLeafAlsoRemovesContainedMessages()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Emails.AddAsync(new EmailDB { Id = Guid.CreateVersion7(), FolderId = GatewayEwsFixtureDomain.GrandchildId, Uid = 1 }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        var reply = await WriteAsync(fixture, "DeleteFolder", $"<m:FolderIds><t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/></m:FolderIds>").ConfigureAwait(false);
        AssertCode(reply, "NoError");
        Assert.IsFalse(await database.Folders.AsNoTracking().AnyAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
        Assert.IsFalse(await database.Emails.AsNoTracking().AnyAsync(item => item.FolderId == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
        await AssertJmapFolderAsync(fixture, GatewayEwsFixtureDomain.GrandchildId, null).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CreateCannotOverrunTheAdvertisedHierarchyCapacity()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, additionalFolders: 493).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(await WriteAsync(fixture, "CreateFolder", CreateFields("<t:DistinguishedFolderId Id='msgfolderroot'/>", "Overflow")).ConfigureAwait(false), "ErrorExceededFindCountLimit");
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("root", "ErrorAccessDenied")]
    [DataRow("protected", "ErrorAccessDenied")]
    [DataRow("children", "ErrorCannotDeleteObject")]
    [DataRow("foreign", "ErrorAccessDenied")]
    [DataRow("forged", "ErrorFolderNotFound")]
    [DataRow("alias", "ErrorAccessDenied")]
    public async Task RefusedDeleteTargetsNeverReachTheMutationOperation(string target, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var reference = target switch
        {
            "root" => "<t:DistinguishedFolderId Id='msgfolderroot'/>",
            "protected" => "<t:DistinguishedFolderId Id='inbox'/>",
            "children" => $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>",
            "foreign" => $"<t:FolderId Id='{GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, GatewayEwsFixtureDomain.ForeignFolderId)}'/>",
            "forged" => $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ForeignFolderId)}'/>",
            _ => "<t:DistinguishedFolderId Id='inbox'><t:Mailbox><t:EmailAddress>alias@example.test</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>",
        };
        AssertCode(await WriteAsync(fixture, "DeleteFolder", $"<m:FolderIds>{reference}</m:FolderIds>").ConfigureAwait(false), code);
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("SoftDelete")]
    [DataRow("MoveToDeletedItems")]
    public async Task UnsupportedDeleteModesAreJournaledWithoutAnyWorkerDispatch(string mode)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(MutationRequest("DeleteFolder", "<m:FolderIds><t:DistinguishedFolderId Id='inbox'/></m:FolderIds>")
            .Replace("HardDelete", mode, StringComparison.Ordinal));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(body, "ErrorInvalidRequest");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("!invalid", "ErrorInvalidChangeKey")]
    [DataRow("c3RhbGU=", "ErrorIrresolvableConflict")]
    public async Task MalformedOrStaleChangeKeysCannotRename(string key, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(await WriteAsync(fixture, "UpdateFolder", UpdateFields(FolderId(GatewayEwsFixtureDomain.GrandchildId), "Wrong", key)).ConfigureAwait(false), code);
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual("INBOX/A & B/Deep", (await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task SameVersionConcurrentRequestsAllowOnlyOneRename()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var id = FolderId(GatewayEwsFixtureDomain.GrandchildId);
        var key = await ReadKeyAsync(fixture, id).ConfigureAwait(false);
        var replies = await Task.WhenAll(ConcurrentRenameAsync(fixture, id, "WinnerA", key),
            ConcurrentRenameAsync(fixture, id, "WinnerB", key)).ConfigureAwait(false);
        Assert.HasCount(1, replies.Where(code => string.Equals(code, "NoError", StringComparison.Ordinal)));
        Assert.IsTrue(replies.All(code => code is "NoError" or "ErrorIrresolvableConflict" or "ErrorServerBusy"));
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var name = (await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name;
        Assert.IsTrue(name is "INBOX/A & B/WinnerA" or "INBOX/A & B/WinnerB");
    }

    [TestMethod]
    public async Task IndependentWorkerTransactionsEnforceTheSameSnapshotAtomically()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var key = await ReadKeyAsync(fixture, FolderId(GatewayEwsFixtureDomain.GrandchildId)).ConfigureAwait(false);
        var state = Encoding.UTF8.GetString(Convert.FromBase64String(key));
        var blocker = Context(fixture);
        await using var blockerLifetime = blocker.ConfigureAwait(false);
        var transaction = await blocker.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM inboxes WHERE id = {GatewayEwsFixtureDomain.AccountId} FOR NO KEY UPDATE").ConfigureAwait(false);
        var first = MutateInIndependentScopeAsync(fixture, state, "IndependentA");
        var second = MutateInIndependentScopeAsync(fixture, state, "IndependentB");
        await WaitForAccountLockAsync(fixture, count: 2).ConfigureAwait(false);
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);
        await transaction.CommitAsync().ConfigureAwait(false);
        var results = await Task.WhenAll(first, second).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(new[] { MailFolderMutationStatus.Ok, MailFolderMutationStatus.StateMismatch }, results);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MixedFolderOrMessageWriterInvalidatesBlockedEwsSnapshot(bool email)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var id = FolderId(GatewayEwsFixtureDomain.GrandchildId);
        var key = await ReadKeyAsync(fixture, id).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var transaction = await database.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        if (email)
            (await database.Emails.FirstAsync(item => item.FolderId == GatewayEwsFixtureDomain.InboxId && !item.IsRead).ConfigureAwait(false)).IsRead = true;
        else
            (await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name = "INBOX/A & B/OtherWriter";
        await database.SaveChangesAsync().ConfigureAwait(false);
        var pending = WriteAsync(fixture, "UpdateFolder", UpdateFields(id, "MustNotWin", key));
        await WaitForAccountLockAsync(fixture).ConfigureAwait(false);
        Assert.IsFalse(pending.IsCompleted);
        await transaction.CommitAsync().ConfigureAwait(false);
        AssertCode(await pending.ConfigureAwait(false), "ErrorIrresolvableConflict");
        database.ChangeTracker.Clear();
        Assert.AreEqual(email ? "INBOX/A & B/Deep" : "INBOX/A & B/OtherWriter",
            (await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task FirstBaselineAndSnapshotWaitForAnUncommittedFolderWriter()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var transaction = await database.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        (await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name = "INBOX/A & B/BeforeBaseline";
        await database.SaveChangesAsync().ConfigureAwait(false);
        var pending = ReadFolderDocumentAsync(fixture, FolderId(GatewayEwsFixtureDomain.GrandchildId));
        await WaitForAccountLockAsync(fixture).ConfigureAwait(false);
        Assert.IsFalse(pending.IsCompleted);
        await transaction.CommitAsync().ConfigureAwait(false);
        var document = await pending.ConfigureAwait(false);
        AssertCode(document, "NoError");
        Assert.AreEqual("BeforeBaseline", document.Descendants(Types + "DisplayName").Single().Value, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MixedWriterCommitRejectsAnAlreadyPreparedWorkerCommand(bool email)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var key = await ReadKeyAsync(fixture, FolderId(GatewayEwsFixtureDomain.GrandchildId)).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var transaction = await database.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        if (email)
            (await database.Emails.FirstAsync(item => item.FolderId == GatewayEwsFixtureDomain.InboxId && !item.IsRead).ConfigureAwait(false)).IsRead = true;
        else
            (await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name = "INBOX/A & B/IndependentWriter";
        await database.SaveChangesAsync().ConfigureAwait(false);
        var pending = MutateInIndependentScopeAsync(fixture, Encoding.UTF8.GetString(Convert.FromBase64String(key)), "CannotOverwrite");
        await WaitForAccountLockAsync(fixture).ConfigureAwait(false);
        Assert.IsFalse(pending.IsCompleted);
        await transaction.CommitAsync().ConfigureAwait(false);
        Assert.AreEqual(MailFolderMutationStatus.StateMismatch, await pending.ConfigureAwait(false));
    }

    [TestMethod]
    public async Task StandaloneConcurrentBaselinesWaitAndInitializeOnlyOnce()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var transaction = await database.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        (await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name = "INBOX/A & B/BaselineWriter";
        await database.SaveChangesAsync().ConfigureAwait(false);
        using var firstScope = fixture.DomainScopes.CreateScope();
        using var secondScope = fixture.DomainScopes.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<JmapStateService>().GetStateAsync(GatewayEwsFixtureDomain.AccountId, "Mailbox");
        var second = secondScope.ServiceProvider.GetRequiredService<JmapStateService>().GetStateAsync(GatewayEwsFixtureDomain.AccountId, "Mailbox");
        await WaitForAccountLockAsync(fixture, count: 2).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        Assert.AreEqual(await first.ConfigureAwait(false), await second.ConfigureAwait(false), StringComparer.Ordinal);
        Assert.AreEqual(1, await database.JmapChanges.CountAsync(item => item.AccountId == GatewayEwsFixtureDomain.AccountId && item.DataType == "_Account").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CoordinationSchemaRepairsDisabledGuardAndPreservesExistingData()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var count = await database.Folders.CountAsync().ConfigureAwait(false);
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE folders DISABLE TRIGGER mk8_folder_account_write_guard").ConfigureAwait(false);
        await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
        await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
        Assert.AreEqual(count, await database.Folders.CountAsync().ConfigureAwait(false));
        Assert.AreEqual("INBOX/A & B/Deep", (await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
        var enabled = await database.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM pg_trigger WHERE tgname IN ('mk8_folder_account_write_guard', 'mk8_email_account_write_guard') AND tgenabled='O'").SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(2, enabled);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task FailedJournalWithholdsWriteAcknowledgement(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(MutationRequest("CreateFolder", CreateFields("<t:DistinguishedFolderId Id='msgfolderroot'/>", "DurableWrite")));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(body, "ErrorServerBusy");
        Assert.IsFalse(body.Contains("NoError", StringComparison.Ordinal));
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        // An outbound failure withholds the acknowledgement, not an already committed domain write.
        Assert.AreEqual(sequence == 1, await database.Folders.AnyAsync(item => item.Name == "DurableWrite").ConfigureAwait(false));
        if (sequence == 0) await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    private static async Task WaitForAccountLockAsync(CaptureFixture fixture, int count = 1)
    {
        var connection = new NpgsqlConnection(fixture.ApplicationConnection);
        await using var lifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var command = connection.CreateCommand();
            await using var commandLifetime = command.ConfigureAwait(false);
            command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%FOR NO KEY UPDATE%'";
            if ((long)(await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false))! >= count) return;
            await Task.Delay(25, deadline.Token).ConfigureAwait(false);
        }
    }

    private static async Task<MailFolderMutationStatus> MutateInIndependentScopeAsync(CaptureFixture fixture, string state, string name)
    {
        using var scope = fixture.DomainScopes.CreateScope();
        var patch = new MailFolderPatch(MailFolderFields.Name, new(name, null, null, 0, false), [], null);
        var command = new MailFolderMutationCommand(GatewayEwsFixtureDomain.AccountId, state, true, [],
            [new($"M{GatewayEwsFixtureDomain.GrandchildId:N}", patch)], []);
        var arguments = (JsonObject)JsonSerializer.SerializeToNode(command, JsonSerializerOptions.Web)!;
        var operation = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages], MailOperationKind.MutateFolders,
            arguments, new Dictionary<string, string>(StringComparer.Ordinal));
        var result = await scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>().ExecuteAsync(operation,
            new AuthenticatedMailUser(GatewayEwsFixtureDomain.OwnerId, "owner@example.test"), Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.MutateFolders, result.Response.Operation);
        var decoded = ApplicationValueCodec.Decode(result.Response.Data!)!.Deserialize<MailFolderMutationResult>(JsonSerializerOptions.Web)!;
        return decoded.Status;
    }

    private static EmailDbContext Context(CaptureFixture fixture) =>
        new(new DbContextOptionsBuilder<EmailDbContext>().UseNpgsql(fixture.ApplicationConnection).Options);

    private static string FolderId(Guid folder) => GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.AccountId, folder);

    private static string MutationRequest(string operation, string fields) =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'><s:Body><m:{operation}{(operation is "DeleteFolder" ? " DeleteType='HardDelete'" : "")}>{fields}</m:{operation}></s:Body></s:Envelope>";

    private static string CreateFields(string parent, string name) =>
        $"<m:ParentFolderId>{parent}</m:ParentFolderId><m:Folders><t:Folder><t:DisplayName>{name}</t:DisplayName></t:Folder></m:Folders>";

    private static string UpdateFields(string id, string name, string? key) =>
        $"<m:FolderChanges><t:FolderChange><t:FolderId Id='{id}'{(key is null ? "" : $" ChangeKey='{key}'")}/><t:Updates><t:SetFolderField><t:FieldURI FieldURI='folder:DisplayName'/><t:Folder><t:DisplayName>{name}</t:DisplayName></t:Folder></t:SetFolderField></t:Updates></t:FolderChange></m:FolderChanges>";

    private static async Task<XDocument> WriteAsync(CaptureFixture fixture, string operation, string fields)
    {
        using var content = XmlContent(MutationRequest(operation, fields));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        return XDocument.Parse(body);
    }

    private static async Task<string> ConcurrentRenameAsync(CaptureFixture fixture, string id, string name, string key)
    {
        using var content = XmlContent(MutationRequest("UpdateFolder", UpdateFields(id, name, key)));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        // A competing commit between count and snapshot legitimately fails that
        // preflight with busy rather than reaching the later state comparison.
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            AssertFault(body, "ErrorServerBusy");
            return "ErrorServerBusy";
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        return XDocument.Parse(body).Descendants(Messages + "ResponseCode").Single().Value;
    }

    private static void AssertCode(XDocument document, string code) =>
        Assert.AreEqual(code, document.Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);

    private static async Task<string> ReadKeyAsync(CaptureFixture fixture, string id)
    {
        using var content = XmlContent(Request("GetFolder", $"<t:FolderId Id='{id}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        return (string)XDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Descendants(Types + "FolderId").Single().Attribute("ChangeKey")!;
    }

    private static async Task<XDocument> ReadFolderDocumentAsync(CaptureFixture fixture, string id)
    {
        using var content = XmlContent(Request("GetFolder", $"<t:FolderId Id='{id}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        return XDocument.Parse(body);
    }

    private static async Task AssertFolderAsync(CaptureFixture fixture, string id, string name, string key)
    {
        using var content = XmlContent(Request("GetFolder", $"<t:FolderId Id='{id}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertCode(document, "NoError");
        Assert.AreEqual(name, document.Descendants(Types + "DisplayName").Single().Value, StringComparer.Ordinal);
        Assert.AreEqual(key, (string?)document.Descendants(Types + "FolderId").Single().Attribute("ChangeKey"), StringComparer.Ordinal);
    }

    private static async Task AssertJmapFolderAsync(CaptureFixture fixture, Guid id, string? name)
    {
        var json = $$"""{"using":["urn:ietf:params:jmap:core","urn:ietf:params:jmap:mail"],"methodCalls":[["Mailbox/get",{"accountId":"A{{GatewayEwsFixtureDomain.AccountId:N}}","ids":["M{{id:N}}"]},"read"]]}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync(new Uri("/jmap/api", UriKind.Relative), content).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var result = document.RootElement.GetProperty("methodResponses")[0];
        Assert.AreEqual("Mailbox/get", result[0].GetString(), StringComparer.Ordinal);
        if (name is null) Assert.AreEqual($"M{id:N}", result[1].GetProperty("notFound")[0].GetString(), StringComparer.Ordinal);
        else Assert.AreEqual(name, result[1].GetProperty("list")[0].GetProperty("name").GetString(), StringComparer.Ordinal);
    }
}
