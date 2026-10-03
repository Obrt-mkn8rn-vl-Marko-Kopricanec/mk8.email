using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Storage;
using mk8.email.Hosting;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.TestSupport;
using mk8.email.Storage;
using mk8.email.Wake;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class JmapDurableReplayTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The TypedEmailSetCreateUpdateDestroyReplaysWithoutDuplicatingAzureContent scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task TypedEmailSetCreateUpdateDestroyReplaysWithoutDuplicatingAzureContent()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var folderId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.Folders.AddAsync(new FolderDB
            {
                Id = folderId,
                InboxId = rig.InboxId,
                Name = "Drafts",
                UidValidity = 1,
                NextUid = 1,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var create = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.MutateMessages, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["create"] = new JsonObject
                {
                    ["draft"] = new JsonObject
                    {
                        ["mailboxIds"] = new JsonObject { [JmapId.Mailbox(folderId)] = true },
                        ["subject"] = "Durable draft",
                        ["bodyValues"] = new JsonObject { ["1"] = new JsonObject { ["value"] = "Stored in Blob" } },
                        ["textBody"] = new JsonArray(new JsonObject { ["partId"] = "1", ["type"] = "text/plain" }),
                    },
                },
            }, "create")], new Dictionary<string, string>(StringComparer.Ordinal));
        var createOperation = Guid.CreateVersion7();
        var firstCreate = await rig.InvokeAsync(create, createOperation).ConfigureAwait(false);
        var emailId = firstCreate.Invocations[0].Arguments["created"]!["draft"]!["id"]!.GetValue<string>();
        Assert.AreEqual(emailId, firstCreate.CreatedIds!["draft"], StringComparer.Ordinal);
        var replayCreate = await rig.InvokeAsync(create, createOperation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(firstCreate.Invocations), JsonSerializer.Serialize(replayCreate.Invocations), StringComparer.Ordinal);
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            var stored = await database.Emails.SingleAsync().ConfigureAwait(false);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, stored.RawMessageObjectProvider, StringComparer.Ordinal);
            Assert.IsNull(stored.RawMessage);
            Assert.IsFalse(stored.IsRead);
        }
        var update = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.MutateMessages, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["update"] = new JsonObject
                {
                    [emailId] = new JsonObject
                    {
                        ["keywords/$seen"] = true,
                        ["subject"] = "Durable draft",
                        ["bodyValues/1/value"] = "Stored in Blob",
                    },
                },
            }, "update")]);
        var updateOperation = Guid.CreateVersion7();
        var firstUpdate = await rig.InvokeAsync(update, updateOperation).ConfigureAwait(false);
        Assert.IsNull(firstUpdate.Invocations[0].Arguments["updated"]![emailId]);
        var replayUpdate = await rig.InvokeAsync(update, updateOperation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(firstUpdate.Invocations), JsonSerializer.Serialize(replayUpdate.Invocations), StringComparer.Ordinal);
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.IsTrue((await database.Emails.SingleAsync().ConfigureAwait(false)).IsRead);
        }
        var destroy = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.MutateMessages, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["destroy"] = new JsonArray(emailId),
            }, "destroy")]);
        var destroyOperation = Guid.CreateVersion7();
        var firstDestroy = await rig.InvokeAsync(destroy, destroyOperation).ConfigureAwait(false);
        Assert.AreEqual(emailId, firstDestroy.Invocations[0].Arguments["destroyed"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        var replayDestroy = await rig.InvokeAsync(destroy, destroyOperation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(firstDestroy.Invocations), JsonSerializer.Serialize(replayDestroy.Invocations), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(0, await verification.Emails.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(3, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The TypedSubmissionSetAndImplicitEmailSetReplayOneCommittedAzureQueueWrite scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task TypedSubmissionSetAndImplicitEmailSetReplayOneCommittedAzureQueueWrite()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var draftsId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.Folders.AddAsync(new FolderDB
            {
                Id = draftsId,
                InboxId = rig.InboxId,
                Name = "Drafts",
                UidValidity = 1,
                NextUid = 1,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var draft = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.MutateMessages, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["create"] = new JsonObject
                {
                    ["draft"] = new JsonObject
                    {
                        ["mailboxIds"] = new JsonObject { [JmapId.Mailbox(draftsId)] = true },
                        ["from"] = new JsonArray(new JsonObject { ["email"] = rig.User.Username }),
                        ["to"] = new JsonArray(new JsonObject { ["email"] = rig.User.Username }),
                        ["subject"] = "Durable submission",
                        ["bodyValues"] = new JsonObject { ["1"] = new JsonObject { ["value"] = "Blob-backed mail" } },
                        ["textBody"] = new JsonArray(new JsonObject { ["partId"] = "1", ["type"] = "text/plain" }),
                    },
                },
            }, "draft")]);
        var createdDraft = await rig.InvokeAsync(draft, Guid.CreateVersion7()).ConfigureAwait(false);
        var emailId = createdDraft.Invocations[0].Arguments["created"]!["draft"]!["id"]!.GetValue<string>();
        var submission = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.MutateSubmissions, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["create"] = new JsonObject
                {
                    ["out"] = new JsonObject
                    {
                        ["identityId"] = JmapId.Identity(rig.InboxId),
                        ["emailId"] = emailId,
                    },
                },
                ["onSuccessUpdateEmail"] = new JsonObject
                {
                    ["#out"] = new JsonObject
                    {
                        ["keywords/$seen"] = true,
                        ["subject"] = "Durable submission",
                        ["bodyValues/1/value"] = "Blob-backed mail",
                    },
                },
            }, "submission")], new Dictionary<string, string>(StringComparer.Ordinal));
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(submission, operation).ConfigureAwait(false);
        Assert.HasCount(2, first.Invocations);
        Assert.AreEqual(MailOperationKind.MutateSubmissions, first.Invocations[0].Operation);
        Assert.AreEqual(MailOperationKind.MutateMessages, first.Invocations[1].Operation);
        var submissionId = first.Invocations[0].Arguments["created"]!["out"]!["id"]!.GetValue<string>();
        Assert.AreEqual(submissionId, first.CreatedIds!["out"], StringComparer.Ordinal);
        Assert.IsNull(first.Invocations[1].Arguments["updated"]![emailId]);
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(1, await database.JmapEmailSubmissions.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(1, await database.MailQueueMessages.CountAsync().ConfigureAwait(false));
            var persisted = await database.JmapEmailSubmissions.SingleAsync().ConfigureAwait(false);
            Assert.AreEqual(GatewayJmapDateCodec.FormatUtc(persisted.SendAt),
                first.Invocations[0].Arguments["created"]!["out"]!["sendAt"]!.GetValue<string>(), StringComparer.Ordinal);
            var queue = await database.MailQueueMessages.SingleAsync().ConfigureAwait(false);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, queue.RawMessageObjectProvider, StringComparer.Ordinal);
            Assert.IsNull(queue.RawMessage);
            Assert.IsTrue((await database.Emails.SingleAsync().ConfigureAwait(false)).IsRead);
        }
        var replay = await rig.InvokeAsync(submission, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        Assert.AreEqual(submissionId, replay.CreatedIds!["out"], StringComparer.Ordinal);
        var assertionUpdate = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.MutateSubmissions, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["update"] = new JsonObject { [submissionId] = first.Invocations[0].Arguments["created"]!["out"]!.DeepClone() },
                ["onSuccessUpdateEmail"] = new JsonObject
                    { [submissionId] = new JsonObject { ["keywords/$flagged"] = true } },
            }, "assertion")]);
        var updateOperation = Guid.CreateVersion7();
        var firstUpdate = await rig.InvokeAsync(assertionUpdate, updateOperation).ConfigureAwait(false);
        Assert.IsNull(firstUpdate.Invocations[0].Arguments["notUpdated"], firstUpdate.Invocations[0].Arguments.ToJsonString());
        Assert.IsNotNull(firstUpdate.Invocations[0].Arguments["updated"], firstUpdate.Invocations[0].Arguments.ToJsonString());
        Assert.IsNull(firstUpdate.Invocations[0].Arguments["updated"]![submissionId]);
        var replayUpdate = await rig.InvokeAsync(assertionUpdate, updateOperation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(firstUpdate.Invocations), JsonSerializer.Serialize(replayUpdate.Invocations), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(1, await verification.JmapEmailSubmissions.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(1, await verification.MailQueueMessages.CountAsync().ConfigureAwait(false));
        Assert.IsTrue((await verification.Emails.SingleAsync().ConfigureAwait(false)).IsFlagged);
        Assert.AreEqual(3, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The TypedBlobCopyUsesAzureStorageAndReplaysCommittedResultWithoutDuplicatingBlob scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task TypedBlobCopyUsesAzureStorageAndReplaysCommittedResultWithoutDuplicatingBlob()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var targetAccountId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            var original = await setup.Inboxes.SingleAsync().ConfigureAwait(false);
            await (setup.Inboxes.AddAsync(new InboxDB
            {
                Id = targetAccountId,
                Name = "copy-target",
                AddressId = original.AddressId,
                OwnerId = rig.User.Id,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        string sourceBlobId;
        using (var scope = rig.Services.CreateScope())
        {
            var blobs = scope.ServiceProvider.GetRequiredService<JmapBlobService>();
            var source = await blobs.StoreAsync(rig.InboxId, "copied payload"u8.ToArray(),
                "text/plain", "payload.txt", CancellationToken.None).ConfigureAwait(false);
            sourceBlobId = source.BlobId;
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic],
            [new JmapApplicationCall(MailOperationKind.CopyBinaryObjects, new JsonObject
            {
                ["fromAccountId"] = JmapId.Account(rig.InboxId),
                ["accountId"] = JmapId.Account(targetAccountId),
                ["blobIds"] = new JsonArray(sourceBlobId, "Umissing"),
            }, "copy")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.CopyBinaryObjects, first.Invocations[0].Operation);
        var copiedBlobId = first.Invocations[0].Arguments["copied"]![sourceBlobId]!.GetValue<string>();
        Assert.AreEqual("notFound", first.Invocations[0].Arguments["notCopied"]!["Umissing"]!["type"]!
            .GetValue<string>(), StringComparer.Ordinal);
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(2, await database.JmapBlobs.CountAsync().ConfigureAwait(false));
            var copied = await database.JmapBlobs.SingleAsync(blob => blob.AccountId == targetAccountId).ConfigureAwait(false);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, copied.ObjectProvider, StringComparer.Ordinal);
            Assert.IsNull(copied.Content);
        }
        using (var scope = rig.Services.CreateScope())
        {
            var blobs = scope.ServiceProvider.GetRequiredService<JmapBlobService>();
            var copied = await blobs.GetAsync(targetAccountId, copiedBlobId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(copied);
            CollectionAssert.AreEqual("copied payload"u8.ToArray(), copied.Content);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(2, await database.JmapBlobs.CountAsync().ConfigureAwait(false));
        }
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreNotEqual(copiedBlobId, fresh.Invocations[0].Arguments["copied"]![sourceBlobId]!.GetValue<string>(), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(3, await verification.JmapBlobs.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The TypedEmailImportUsesAzureContentAndReplaysWithoutDuplicatingMessage scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task TypedEmailImportUsesAzureContentAndReplaysWithoutDuplicatingMessage()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var folderId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.Folders.AddAsync(new FolderDB
            {
                Id = folderId,
                InboxId = rig.InboxId,
                Name = "Inbox",
                UidValidity = 1,
                NextUid = 1,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        const string raw = "From: sender@example.test\r\nTo: replay@example.test\r\nSubject: Imported\r\n\r\nBody";
        string blobId;
        using (var scope = rig.Services.CreateScope())
        {
            var blobs = scope.ServiceProvider.GetRequiredService<JmapBlobService>();
            var stored = await blobs.StoreAsync(rig.InboxId, Encoding.UTF8.GetBytes(raw),
                "message/rfc822", "import.eml", CancellationToken.None).ConfigureAwait(false);
            blobId = stored.BlobId;
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.ImportMessages, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["emails"] = new JsonObject
                {
                    ["created"] = new JsonObject
                    {
                        ["blobId"] = blobId,
                        ["mailboxIds"] = new JsonObject { [JmapId.Mailbox(folderId)] = true },
                        ["keywords"] = new JsonObject { ["$seen"] = true },
                    },
                },
            }, "import")], new Dictionary<string, string>(StringComparer.Ordinal));
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        var emailId = first.Invocations[0].Arguments["created"]!["created"]!["id"]!.GetValue<string>();
        Assert.AreEqual(emailId, first.CreatedIds!["created"], StringComparer.Ordinal);
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(1, await database.Emails.CountAsync().ConfigureAwait(false));
            var email = await database.Emails.SingleAsync().ConfigureAwait(false);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, email.RawMessageObjectProvider, StringComparer.Ordinal);
            Assert.IsNull(email.RawMessage);
            Assert.IsTrue(email.IsRead);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        Assert.AreEqual(emailId, replay.CreatedIds!["created"], StringComparer.Ordinal);
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(1, await database.Emails.CountAsync().ConfigureAwait(false));
        }
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreNotEqual(emailId, fresh.Invocations[0].Arguments["created"]!["created"]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        var final = rig.Context();
        await using var finalLifetime = final.ConfigureAwait(false);
        Assert.AreEqual(2, await final.Emails.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(2, await final.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The TypedEmailReadAndParseReplayAzureBackedProjectionSnapshots scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task TypedEmailReadAndParseReplayAzureBackedProjectionSnapshots()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var folderId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.Folders.AddAsync(new FolderDB
            {
                Id = folderId,
                InboxId = rig.InboxId,
                Name = "Inbox",
                UidValidity = 1,
                NextUid = 1,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        const string raw = "From: sender@example.test\r\nTo: replay@example.test\r\nSubject: Azure projection\r\n\r\nBody";
        string blobId;
        using (var scope = rig.Services.CreateScope())
        {
            var blobs = scope.ServiceProvider.GetRequiredService<JmapBlobService>();
            blobId = (await blobs.StoreAsync(rig.InboxId, Encoding.UTF8.GetBytes(raw),
                "message/rfc822", "projection.eml", CancellationToken.None).ConfigureAwait(false)).BlobId;
        }
        var imported = await rig.InvokeAsync(new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.ImportMessages, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["emails"] = new JsonObject
                {
                    ["created"] = new JsonObject
                    {
                        ["blobId"] = blobId,
                        ["mailboxIds"] = new JsonObject { [JmapId.Mailbox(folderId)] = true },
                    },
                },
            }, "import")], new Dictionary<string, string>(StringComparer.Ordinal)), Guid.CreateVersion7()).ConfigureAwait(false);
        var emailId = imported.Invocations[0].Arguments["created"]!["created"]!["id"]!.GetValue<string>();
        var read = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.ReadMessages, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["ids"] = new JsonArray(emailId, "missing"),
                ["properties"] = new JsonArray("id", "subject"),
            }, "read")]);
        var readOperation = Guid.CreateVersion7();
        var firstRead = await rig.InvokeAsync(read, readOperation).ConfigureAwait(false);
        Assert.AreEqual("Azure projection", firstRead.Invocations[0].Arguments["list"]![0]!["subject"]!
            .GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("missing", firstRead.Invocations[0].Arguments["notFound"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        var replayRead = await rig.InvokeAsync(read, readOperation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(firstRead.Invocations), JsonSerializer.Serialize(replayRead.Invocations), StringComparer.Ordinal);
        var parse = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.ParseMessages, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["blobIds"] = new JsonArray(blobId, "Umissing"),
                ["properties"] = new JsonArray("subject"),
            }, "parse")]);
        var parseOperation = Guid.CreateVersion7();
        var firstParse = await rig.InvokeAsync(parse, parseOperation).ConfigureAwait(false);
        Assert.AreEqual("Azure projection", firstParse.Invocations[0].Arguments["parsed"]![blobId]!["subject"]!
            .GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Umissing", firstParse.Invocations[0].Arguments["notFound"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        var replayParse = await rig.InvokeAsync(parse, parseOperation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(firstParse.Invocations), JsonSerializer.Serialize(replayParse.Invocations), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        var stored = await verification.Emails.SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(LargeObjectProviders.AzureBlob, stored.RawMessageObjectProvider, StringComparer.Ordinal);
        Assert.IsNull(stored.RawMessage);
        Assert.AreEqual(3, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The TypedEmailCopyReplaysCommittedCopyAndSourceDeletionWithoutRepeatingEither scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task TypedEmailCopyReplaysCommittedCopyAndSourceDeletionWithoutRepeatingEither()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var sourceFolderId = Guid.CreateVersion7();
        var targetAccountId = Guid.CreateVersion7();
        var targetFolderId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            var original = await setup.Inboxes.SingleAsync().ConfigureAwait(false);
            await (setup.Inboxes.AddAsync(new InboxDB
            {
                Id = targetAccountId,
                Name = "copy-target",
                AddressId = original.AddressId,
                OwnerId = rig.User.Id,
            })).ConfigureAwait(false);
            await (setup.Folders.AddAsync(new FolderDB
            {
                Id = sourceFolderId,
                InboxId = rig.InboxId,
                Name = "Source",
                UidValidity = 1,
                NextUid = 1,
            })).ConfigureAwait(false);
            await (setup.Folders.AddAsync(new FolderDB
            {
                Id = targetFolderId,
                InboxId = targetAccountId,
                Name = "Target",
                UidValidity = 1,
                NextUid = 1,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        const string raw = "From: sender@example.test\r\nTo: replay@example.test\r\nSubject: Copy\r\n\r\nBody";
        string blobId;
        using (var scope = rig.Services.CreateScope())
        {
            var blobs = scope.ServiceProvider.GetRequiredService<JmapBlobService>();
            blobId = (await blobs.StoreAsync(rig.InboxId, Encoding.UTF8.GetBytes(raw),
                "message/rfc822", "copy.eml", CancellationToken.None).ConfigureAwait(false)).BlobId;
        }
        var import = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.ImportMessages, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["emails"] = new JsonObject
                {
                    ["source"] = new JsonObject
                    {
                        ["blobId"] = blobId,
                        ["mailboxIds"] = new JsonObject { [JmapId.Mailbox(sourceFolderId)] = true },
                    },
                },
            }, "import")], new Dictionary<string, string>(StringComparer.Ordinal));
        var imported = await rig.InvokeAsync(import, Guid.CreateVersion7()).ConfigureAwait(false);
        var sourceId = imported.Invocations[0].Arguments["created"]!["source"]!["id"]!.GetValue<string>();
        var copy = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.CopyMessages, new JsonObject
            {
                ["fromAccountId"] = JmapId.Account(rig.InboxId),
                ["accountId"] = JmapId.Account(targetAccountId),
                ["create"] = new JsonObject
                {
                    ["copied"] = new JsonObject
                    {
                        ["id"] = sourceId,
                        ["mailboxIds"] = new JsonObject { [JmapId.Mailbox(targetFolderId)] = true },
                    },
                },
                ["onSuccessDestroyOriginal"] = true,
            }, "copy")], new Dictionary<string, string>(StringComparer.Ordinal));
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(copy, operation).ConfigureAwait(false);
        Assert.HasCount(2, first.Invocations);
        Assert.AreEqual(MailOperationKind.CopyMessages, first.Invocations[0].Operation);
        Assert.AreEqual(MailOperationKind.MutateMessages, first.Invocations[1].Operation);
        var copiedId = first.Invocations[0].Arguments["created"]!["copied"]!["id"]!.GetValue<string>();
        Assert.AreEqual(copiedId, first.CreatedIds!["copied"], StringComparer.Ordinal);
        Assert.AreEqual(sourceId, first.Invocations[1].Arguments["destroyed"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(1, await database.Emails.CountAsync().ConfigureAwait(false));
            var target = await database.Emails.SingleAsync().ConfigureAwait(false);
            Assert.AreEqual(targetFolderId, target.FolderId);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, target.RawMessageObjectProvider, StringComparer.Ordinal);
            Assert.IsNull(target.RawMessage);
        }
        var replay = await rig.InvokeAsync(copy, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        Assert.AreEqual(copiedId, replay.CreatedIds!["copied"], StringComparer.Ordinal);
        var final = rig.Context();
        await using var finalLifetime = final.ConfigureAwait(false);
        Assert.AreEqual(1, await final.Emails.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(2, await final.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedContactCopyPreservesAccountPrecedenceAndReplaysCommittedError()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var otherAccountId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            var primary = await setup.Inboxes.SingleAsync().ConfigureAwait(false);
            await (setup.Inboxes.AddAsync(new InboxDB
            {
                Id = otherAccountId,
                Name = "other",
                AddressId = primary.AddressId,
                OwnerId = rig.User.Id,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var arguments = new JsonObject
        {
            ["fromAccountId"] = JmapId.Account(rig.InboxId),
            ["accountId"] = JmapId.Account(otherAccountId),
            ["create"] = new JsonObject { ["copy"] = new JsonObject { ["id"] = "Csource" } },
        };
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Contacts],
            [new JmapApplicationCall(MailOperationKind.CopyContacts, (JsonObject)arguments.DeepClone(), "copy")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.Failure, first.Invocations[0].Operation);
        Assert.AreEqual("accountNotSupportedByMethod", first.Invocations[0].Arguments["type"]!.GetValue<string>(), StringComparer.Ordinal);
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        arguments["fromAccountId"] = JmapId.Account(otherAccountId);
        arguments["accountId"] = JmapId.Account(rig.InboxId);
        var reversed = await rig.InvokeAsync(new JmapApplicationBatch([MailFeature.Basic, MailFeature.Contacts],
            [new JmapApplicationCall(MailOperationKind.CopyContacts, (JsonObject)arguments.DeepClone(), "reversed")]),
            Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual("fromAccountNotSupportedByMethod",
            reversed.Invocations[0].Arguments["type"]!.GetValue<string>(), StringComparer.Ordinal);
        arguments["fromAccountId"] = "Ainvalid";
        var missing = await rig.InvokeAsync(new JmapApplicationBatch([MailFeature.Basic, MailFeature.Contacts],
            [new JmapApplicationCall(MailOperationKind.CopyContacts, arguments, "missing")]),
            Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual("fromAccountNotFound", missing.Invocations[0].Arguments["type"]!.GetValue<string>(), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(3, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedContactQueriesReplayCommittedSearchAndChangeSnapshots()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var initialBatch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Contacts],
            [new JmapApplicationCall(MailOperationKind.FindContacts, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
            }, "initial")]);
        var initial = await rig.InvokeAsync(initialBatch, Guid.CreateVersion7()).ConfigureAwait(false);
        var since = initial.Invocations[0].Arguments["queryState"]!.GetValue<string>();
        var created = await rig.InvokeAsync(rig.ContactsBatch(), Guid.CreateVersion7()).ConfigureAwait(false);
        var cardId = created.Invocations[1].Arguments["created"]!["card"]!["id"]!.GetValue<string>();
        var query = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Contacts],
            [new JmapApplicationCall(MailOperationKind.FindContacts, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["filter"] = new JsonObject { ["name"] = "Replay" },
                ["sort"] = new JsonArray(new JsonObject { ["property"] = "name/given" }),
                ["calculateTotal"] = true,
            }, "query")]);
        var changes = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Contacts],
            [new JmapApplicationCall(MailOperationKind.FindContactChanges, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["sinceQueryState"] = since,
                ["filter"] = new JsonObject { ["name"] = "Replay" },
                ["calculateTotal"] = true,
            }, "changes")]);
        var queryOperation = Guid.CreateVersion7();
        var changesOperation = Guid.CreateVersion7();
        var firstQuery = await rig.InvokeAsync(query, queryOperation).ConfigureAwait(false);
        var firstChanges = await rig.InvokeAsync(changes, changesOperation).ConfigureAwait(false);
        Assert.AreEqual(cardId, firstQuery.Invocations[0].Arguments["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(cardId, firstChanges.Invocations[0].Arguments["added"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        var changed = await rig.InvokeAsync(new JmapApplicationBatch([MailFeature.Basic, MailFeature.Contacts],
            [new JmapApplicationCall(MailOperationKind.MutateContacts, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["update"] = new JsonObject { [cardId] = new JsonObject
                {
                    ["name"] = new JsonObject { ["@type"] = "Name", ["full"] = "Other person" },
                } },
            }, "update")]), Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.IsTrue(changed.Invocations[0].Arguments["updated"]!.AsObject().ContainsKey(cardId));
        var replayQuery = await rig.InvokeAsync(query, queryOperation).ConfigureAwait(false);
        var replayChanges = await rig.InvokeAsync(changes, changesOperation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(firstQuery.Invocations), JsonSerializer.Serialize(replayQuery.Invocations), StringComparer.Ordinal);
        Assert.AreEqual(JsonSerializer.Serialize(firstChanges.Invocations), JsonSerializer.Serialize(replayChanges.Invocations), StringComparer.Ordinal);
        var freshQuery = await rig.InvokeAsync(query, Guid.CreateVersion7()).ConfigureAwait(false);
        var freshChanges = await rig.InvokeAsync(changes, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual(0, freshQuery.Invocations[0].Arguments["ids"]!.AsArray().Count);
        Assert.AreEqual(0, freshChanges.Invocations[0].Arguments["added"]!.AsArray().Count);
        Assert.AreNotEqual(firstChanges.Invocations[0].Arguments["newQueryState"]!.GetValue<string>(),
            freshChanges.Invocations[0].Arguments["newQueryState"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task TypedContactReadReplaysCommittedJsContactProjection()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var created = await rig.InvokeAsync(rig.ContactsBatch(), Guid.CreateVersion7()).ConfigureAwait(false);
        var cardId = created.Invocations[1].Arguments["created"]!["card"]!["id"]!.GetValue<string>();
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Contacts],
            [new JmapApplicationCall(MailOperationKind.ReadContacts, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["ids"] = new JsonArray(cardId),
                ["properties"] = new JsonArray("uid", "name", "addressBookIds"),
            }, "read")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        var firstCard = first.Invocations[0].Arguments["list"]![0]!;
        Assert.AreEqual("Replay person", firstCard["name"]!["full"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsFalse(firstCard.AsObject().ContainsKey("version"));
        Assert.IsNotNull(firstCard["addressBookIds"]);
        var changed = await rig.InvokeAsync(new JmapApplicationBatch([MailFeature.Basic, MailFeature.Contacts],
            [new JmapApplicationCall(MailOperationKind.MutateContacts, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["update"] = new JsonObject { [cardId] = new JsonObject
                {
                    ["name"] = new JsonObject { ["@type"] = "Name", ["full"] = "Updated person" },
                } },
            }, "update")]), Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.IsTrue(changed.Invocations[0].Arguments["updated"]!.AsObject().ContainsKey(cardId));
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual("Updated person", fresh.Invocations[0].Arguments["list"]![0]!["name"]!["full"]!
            .GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task TypedSubmissionReadReplaysCommittedSnapshotAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var submissionId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.JmapEmailSubmissions.AddAsync(new JmapEmailSubmissionDB
            {
                Id = submissionId,
                SubmissionObjectId = JmapId.Submission(submissionId),
                AccountId = rig.InboxId,
                IdentityId = JmapId.Identity(rig.InboxId),
                EmailId = JmapId.Email(submissionId),
                ThreadId = JmapId.Thread(submissionId.ToString("N")),
                QueueId = Guid.CreateVersion7(),
                EnvelopeSender = rig.User.Username,
                EnvelopeRecipients = ["recipient@example.test"],
                SendAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
                UndoStatus = "final",
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.ReadSubmissions, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["ids"] = new JsonArray(JmapId.Submission(submissionId)),
                ["properties"] = new JsonArray("undoStatus", "envelope"),
            }, "submission")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual("final", first.Invocations[0].Arguments["list"]![0]!["undoStatus"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(rig.User.Username,
            first.Invocations[0].Arguments["list"]![0]!["envelope"]!["mailFrom"]!["email"]!.GetValue<string>(), StringComparer.Ordinal);
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var submission = await changed.JmapEmailSubmissions.SingleAsync().ConfigureAwait(false);
            submission.UndoStatus = "pending";
            submission.EnvelopeSender = "updated@example.test";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual("pending", fresh.Invocations[0].Arguments["list"]![0]!["undoStatus"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("updated@example.test",
            fresh.Invocations[0].Arguments["list"]![0]!["envelope"]!["mailFrom"]!["email"]!.GetValue<string>(), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedSubmissionQueryReplaysCommittedFilteredOrderAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var submissionId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.JmapEmailSubmissions.AddAsync(NewSubmission(rig, submissionId, "final"))).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.FindSubmissions, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["filter"] = new JsonObject { ["undoStatus"] = "final" },
                ["calculateTotal"] = true,
            }, "query")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.FindSubmissions, first.Invocations[0].Operation);
        Assert.AreEqual(JmapId.Submission(submissionId), first.Invocations[0].Arguments["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var submission = await changed.JmapEmailSubmissions.SingleAsync().ConfigureAwait(false);
            submission.UndoStatus = "canceled";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual(0, fresh.Invocations[0].Arguments["ids"]!.AsArray().Count);
        Assert.AreEqual(0, fresh.Invocations[0].Arguments["total"]!.GetValue<int>());
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedSubmissionQueryDoesNotMatchOpaqueAnchorToLegacyEmptyGuidRow()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            var legacyRow = NewSubmission(rig, Guid.Empty, "final");
            await (setup.JmapEmailSubmissions.AddAsync(legacyRow)).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
            // EF generates a new key for Guid.Empty, so create the legacy row directly in PostgreSQL.
            await setup.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE jmap_email_submissions SET id = {Guid.Empty} WHERE id = {legacyRow.Id}").ConfigureAwait(false);
        }
        var arguments = new JsonObject
        {
            ["accountId"] = JmapId.Account(rig.InboxId),
            ["anchor"] = JmapId.Submission(Guid.Empty),
        };
        var canonical = await rig.InvokeAsync(new JmapApplicationBatch(
            [MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.FindSubmissions, (JsonObject)arguments.DeepClone(), "canonical")]),
            Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.IsNotNull(canonical.Invocations[0].Arguments["ids"],
            canonical.Invocations[0].Arguments.ToJsonString());
        Assert.AreEqual(JmapId.Submission(Guid.Empty),
            canonical.Invocations[0].Arguments["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        arguments["anchor"] = "opaque";
        var opaque = await rig.InvokeAsync(new JmapApplicationBatch(
            [MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.FindSubmissions, arguments, "opaque")]),
            Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual("anchorNotFound", opaque.Invocations[0].Arguments["type"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task TypedSubmissionQueryChangesReplaysCommittedDeltaAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var query = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.FindSubmissions, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
            }, "initial")]);
        var initial = await rig.InvokeAsync(query, Guid.CreateVersion7()).ConfigureAwait(false);
        var since = initial.Invocations[0].Arguments["queryState"]!.GetValue<string>();
        var submissionId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.JmapEmailSubmissions.AddAsync(NewSubmission(rig, submissionId, "final"))).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.FindSubmissionChanges, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["sinceQueryState"] = since,
                ["filter"] = new JsonObject { ["undoStatus"] = "final" },
                ["calculateTotal"] = true,
            }, "changes")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.FindSubmissionChanges, first.Invocations[0].Operation);
        Assert.AreEqual(JmapId.Submission(submissionId),
            first.Invocations[0].Arguments["added"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var submission = await changed.JmapEmailSubmissions.SingleAsync().ConfigureAwait(false);
            submission.UndoStatus = "canceled";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual(0, fresh.Invocations[0].Arguments["added"]!.AsArray().Count);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(3, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    private static JmapEmailSubmissionDB NewSubmission(Rig rig, Guid id, string undoStatus) => new()
    {
        Id = id,
        SubmissionObjectId = JmapId.Submission(id),
        AccountId = rig.InboxId,
        IdentityId = JmapId.Identity(rig.InboxId),
        EmailId = JmapId.Email(id),
        ThreadId = JmapId.Thread(id.ToString("N")),
        QueueId = Guid.CreateVersion7(),
        EnvelopeSender = rig.User.Username,
        EnvelopeRecipients = ["recipient@example.test"],
        SendAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
        UndoStatus = undoStatus,
    };

    [TestMethod]
    public async Task TypedThreadReadReplaysCommittedGroupingAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var folderId = Guid.CreateVersion7();
        var emailId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.Folders.AddAsync(new FolderDB
            {
                Id = folderId,
                InboxId = rig.InboxId,
                Name = "Inbox",
                UidValidity = 1,
                NextUid = 2,
            })).ConfigureAwait(false);
            await (setup.Emails.AddAsync(new EmailDB
            {
                Id = emailId,
                Sender = "sender@example.test",
                Recipient = rig.User.Username,
                Subject = "Thread receipt",
                Body = "body",
                RawMessage = "hello"u8.ToArray(),
                SizeBytes = 5,
                EmailObjectId = emailId.ToString("N"),
                ThreadObjectId = "c!",
                ReceivedAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
                FolderId = folderId,
                Uid = 1,
                ModSeq = 1,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.ReadThreads, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["properties"] = new JsonArray("emailIds"),
            }, "thread")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual("TYyE", first.Invocations[0].Arguments["list"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(JmapId.Email(emailId),
            first.Invocations[0].Arguments["list"]![0]!["emailIds"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var email = await changed.Emails.SingleAsync().ConfigureAwait(false);
            email.ThreadObjectId = "changed";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual("Tchanged", fresh.Invocations[0].Arguments["list"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedVacationReadReplaysItsCommittedSnapshotAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.AutomaticReplies],
            [new JmapApplicationCall(MailOperationKind.ReadVacationSettings, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["properties"] = new JsonArray("subject"),
            }, "vacation")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.ReadVacationSettings, first.Invocations[0].Operation);
        Assert.IsNull(first.Invocations[0].Arguments["list"]![0]!["subject"]);
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var vacation = await changed.JmapVacationResponses.SingleAsync().ConfigureAwait(false);
            vacation.Subject = "New subject";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual("New subject", fresh.Invocations[0].Arguments["list"]![0]!["subject"]!.GetValue<string>(), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedVacationMutationUsesAzureBlobAndReplaysWithoutRepeatingChanges()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.AutomaticReplies],
            [new JmapApplicationCall(MailOperationKind.MutateVacationSettings, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["update"] = new JsonObject
                {
                    ["singleton"] = new JsonObject
                    {
                        ["isEnabled"] = true,
                        ["subject"] = "Away",
                        ["textBody"] = "Back later",
                    },
                },
            }, "vacation-set")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.MutateVacationSettings, first.Invocations[0].Operation);
        Assert.IsTrue(first.Invocations[0].Arguments["updated"]!.AsObject().ContainsKey("singleton"));
        string? originalBodyObject;
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            var vacation = await database.JmapVacationResponses.SingleAsync().ConfigureAwait(false);
            Assert.AreEqual("Away", vacation.Subject, StringComparer.Ordinal);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, vacation.BodyObjectProvider, StringComparer.Ordinal);
            Assert.IsNull(vacation.TextBody);
            Assert.IsNull(vacation.HtmlBody);
            originalBodyObject = vacation.BodyObjectName;
            vacation.Subject = "Changed after commit";
            await database.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            var vacation = await database.JmapVacationResponses.SingleAsync().ConfigureAwait(false);
            Assert.AreEqual("Changed after commit", vacation.Subject, StringComparer.Ordinal);
            Assert.AreEqual(originalBodyObject, vacation.BodyObjectName, StringComparer.Ordinal);
        }
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.IsTrue(fresh.Invocations[0].Arguments["updated"]!.AsObject().ContainsKey("singleton"));
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual("Away", (await verification.JmapVacationResponses.SingleAsync().ConfigureAwait(false)).Subject, StringComparer.Ordinal);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedPushReadReplaysItsCommittedSnapshotAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var subscriptionId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.JmapPushSubscriptions.AddAsync(new JmapPushSubscriptionDB
            {
                Id = subscriptionId,
                SubscriptionObjectId = JmapId.PushSubscription(subscriptionId),
                UserId = rig.User.Id,
                DeviceClientId = "initial",
                Url = "https://push.example.test/",
                VerificationCode = "private-code",
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic],
            [new JmapApplicationCall(MailOperationKind.ReadNotificationSubscriptions, new JsonObject
            {
                ["properties"] = new JsonArray("deviceClientId"),
            }, "push")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.ReadNotificationSubscriptions, first.Invocations[0].Operation);
        Assert.AreEqual("initial", first.Invocations[0].Arguments["list"]![0]!["deviceClientId"]!.GetValue<string>(), StringComparer.Ordinal);
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var subscription = await changed.JmapPushSubscriptions.SingleAsync().ConfigureAwait(false);
            subscription.DeviceClientId = "updated";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual("updated", fresh.Invocations[0].Arguments["list"]![0]!["deviceClientId"]!.GetValue<string>(), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedIdentityReadReplaysItsCommittedSnapshotAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.ReadSenderIdentities, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["properties"] = new JsonArray("name", "email"),
            }, "identities")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.ReadSenderIdentities, first.Invocations[0].Operation);
        Assert.AreEqual(string.Empty, first.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var identity = await changed.JmapIdentities.SingleAsync().ConfigureAwait(false);
            identity.Name = "Renamed sender";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual("Renamed sender", fresh.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedAddressBookReadReplaysItsCommittedSnapshotAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Contacts],
            [new JmapApplicationCall(MailOperationKind.ReadAddressBooks, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["properties"] = new JsonArray("name"),
            }, "books")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.ReadAddressBooks, first.Invocations[0].Operation);
        Assert.AreEqual("Address Book", first.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var book = await changed.DavCollections.SingleAsync().ConfigureAwait(false);
            book.DisplayName = "Renamed book";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual("Renamed book", fresh.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PostgreSqlChangeReaderPreservesContactAndIdentityLazyDefaults()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        using var scope = rig.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IMailChangesReader>();
        var before = rig.Context();
        await using var beforeLifetime = before.ConfigureAwait(false);
        Assert.AreEqual(0, await before.DavCollections.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await before.JmapIdentities.CountAsync().ConfigureAwait(false));

        var denied = await reader.ReadAsync(MailOperationKind.ReadAddressBookChanges,
            new MailChangesCommand(rig.InboxId, "s0", null, false), rig.User, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailChangesStatus.AccountNotSupported, denied.Status);
        var unchanged = rig.Context();
        await using var unchangedLifetime = unchanged.ConfigureAwait(false);
        Assert.AreEqual(0, await unchanged.DavCollections.CountAsync().ConfigureAwait(false));

        var books = await reader.ReadAsync(MailOperationKind.ReadAddressBookChanges,
            new MailChangesCommand(rig.InboxId, "s0", null, true), rig.User, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailChangesStatus.Ok, books.Status);
        Assert.IsTrue(books.CreatedKeys.Any(key => key.StartsWith('D')));
        var identities = await reader.ReadAsync(MailOperationKind.ReadSenderIdentityChanges,
            new MailChangesCommand(rig.InboxId, "s0", null, true), rig.User, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailChangesStatus.Ok, identities.Status);
        Assert.IsTrue(identities.CreatedKeys.Contains(JmapId.Identity(rig.InboxId), StringComparer.Ordinal));
        var after = rig.Context();
        await using var afterLifetime = after.ConfigureAwait(false);
        Assert.AreEqual(1, await after.DavCollections.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(1, await after.JmapIdentities.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedMailboxChangesReplaysItsCommittedStateAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var folderId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.Folders.AddAsync(new FolderDB
            {
                Id = folderId,
                InboxId = rig.InboxId,
                Name = "Initial",
                UidValidity = 1,
                NextUid = 1,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.ReadFolderChanges, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["sinceState"] = "s0",
            }, "changes")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.ReadFolderChanges, first.Invocations[0].Operation);
        Assert.IsTrue(first.Invocations[0].Arguments["created"]!.AsArray()
            .Any(value => string.Equals(value!.GetValue<string>(), JmapId.Mailbox(folderId), StringComparison.Ordinal)));
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var folder = await changed.Folders.SingleAsync(row => row.Id == folderId).ConfigureAwait(false);
            folder.Name = "Updated";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreNotEqual(first.Invocations[0].Arguments["newState"]!.GetValue<string>(),
            fresh.Invocations[0].Arguments["newState"]!.GetValue<string>(), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedMailboxReadReplaysItsCommittedSnapshotAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var folderId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.Folders.AddAsync(new FolderDB
            {
                Id = folderId,
                InboxId = rig.InboxId,
                Name = "Before retry",
                UidValidity = 1,
                NextUid = 1,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.ReadFolders, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["ids"] = new JsonArray(JmapId.Mailbox(folderId)),
                ["properties"] = new JsonArray("name"),
            }, "get")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual("Before retry", first.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var folder = await changed.Folders.SingleAsync(row => row.Id == folderId).ConfigureAwait(false);
            folder.Name = "After retry";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual("After retry", fresh.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedFolderMutationReplaysCreationAndAtomicNameSwapInPostgres()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
        [
            new(MailOperationKind.MutateFolders, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["create"] = new JsonObject
                {
                    ["alpha"] = new JsonObject { ["name"] = "Alpha", ["role"] = "flagged" },
                    ["beta"] = new JsonObject { ["name"] = "Beta", ["role"] = "important" },
                },
            }, "create"),
            new(MailOperationKind.MutateFolders, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["update"] = new JsonObject
                {
                    ["#alpha"] = new JsonObject { ["name"] = "Beta", ["role"] = "important" },
                    ["#beta"] = new JsonObject { ["name"] = "Alpha", ["role"] = "flagged" },
                },
            }, "swap"),
        ]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.MutateFolders, first.Invocations[0].Operation);
        Assert.AreEqual(MailOperationKind.MutateFolders, first.Invocations[1].Operation,
            first.Invocations[1].Arguments.ToJsonString());
        Assert.IsNull(first.Invocations[0].Arguments["notCreated"]);
        Assert.IsNull(first.Invocations[1].Arguments["notUpdated"]);
        var alphaId = first.Invocations[0].Arguments["created"]!["alpha"]!["id"]!.GetValue<string>();
        var betaId = first.Invocations[0].Arguments["created"]!["beta"]!["id"]!.GetValue<string>();
        Assert.IsTrue(JmapId.TryParseMailbox(alphaId, out var alpha));
        Assert.IsTrue(JmapId.TryParseMailbox(betaId, out var beta));
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual("Beta", (await verification.Folders.SingleAsync(folder => folder.Id == alpha).ConfigureAwait(false)).Name, StringComparer.Ordinal);
        Assert.AreEqual("Alpha", (await verification.Folders.SingleAsync(folder => folder.Id == beta).ConfigureAwait(false)).Name, StringComparer.Ordinal);
        Assert.AreEqual(1, await verification.Folders.CountAsync(folder => folder.InboxId == rig.InboxId && folder.Name == "Alpha").ConfigureAwait(false));
        Assert.AreEqual(1, await verification.Folders.CountAsync(folder => folder.InboxId == rig.InboxId && folder.Name == "Beta").ConfigureAwait(false));
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedFolderQueryReplaysCommittedFilteredOrderAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var folderId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.Folders.AddAsync(new FolderDB
            {
                Id = folderId,
                InboxId = rig.InboxId,
                Name = "Alpha",
                UidValidity = 1,
                NextUid = 1,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.FindFolders, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["filter"] = new JsonObject { ["name"] = "Alpha" },
                ["sort"] = new JsonArray(new JsonObject { ["property"] = "name" }),
                ["calculateTotal"] = true,
            }, "query")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.FindFolders, first.Invocations[0].Operation);
        Assert.AreEqual(JmapId.Mailbox(folderId), first.Invocations[0].Arguments["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var folder = await changed.Folders.SingleAsync(row => row.Id == folderId).ConfigureAwait(false);
            folder.Name = "Beta";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual(0, fresh.Invocations[0].Arguments["ids"]!.AsArray().Count);
        Assert.AreEqual(0, fresh.Invocations[0].Arguments["total"]!.GetValue<int>());
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TypedFolderQueryChangesReplaysCommittedDeltaAcrossWorkerRetries()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var query = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.FindFolders, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
            }, "initial")]);
        var initial = await rig.InvokeAsync(query, Guid.CreateVersion7()).ConfigureAwait(false);
        var since = initial.Invocations[0].Arguments["queryState"]!.GetValue<string>();
        var folderId = Guid.CreateVersion7();
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.Folders.AddAsync(new FolderDB
            {
                Id = folderId,
                InboxId = rig.InboxId,
                Name = "New",
                UidValidity = 1,
                NextUid = 1,
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.FindFolderChanges, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["sinceQueryState"] = since,
                ["calculateTotal"] = true,
            }, "changes")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.FindFolderChanges, first.Invocations[0].Operation);
        Assert.AreEqual(JmapId.Mailbox(folderId),
            first.Invocations[0].Arguments["added"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        {
            var changed = rig.Context();
            await using var changedLifetime = changed.ConfigureAwait(false);
            var folder = await changed.Folders.SingleAsync(row => row.Id == folderId).ConfigureAwait(false);
            folder.Name = "Renamed";
            await changed.SaveChangesAsync().ConfigureAwait(false);
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreNotEqual(first.Invocations[0].Arguments["newQueryState"]!.GetValue<string>(),
            fresh.Invocations[0].Arguments["newQueryState"]!.GetValue<string>(), StringComparer.Ordinal);
        var verification = rig.Context();
        await using var verificationLifetime = verification.ConfigureAwait(false);
        Assert.AreEqual(3, await verification.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task NativeFolderMutationReplaysParentReferencesWithoutDuplicatingFolders()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
        [
            new(MailOperationKind.MutateFolders, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["create"] = new JsonObject
                {
                    ["child"] = new JsonObject { ["name"] = "Child", ["parentId"] = "#parent" },
                    ["parent"] = new JsonObject { ["name"] = "Replay Parent" },
                },
            }, "folders"),
        ], new Dictionary<string, string>(StringComparer.Ordinal));
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.MutateFolders, first.Invocations[0].Operation);
        var parentId = first.Invocations[0].Arguments["created"]!["parent"]!["id"]!.GetValue<string>();
        var childId = first.Invocations[0].Arguments["created"]!["child"]!["id"]!.GetValue<string>();
        Assert.AreEqual(parentId, first.CreatedIds!["parent"], StringComparer.Ordinal);
        Assert.AreEqual(childId, first.CreatedIds["child"], StringComparer.Ordinal);
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(first.CreatedIds.ToArray(), replay.CreatedIds!.ToArray());
        var database = rig.Context();
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(1, await database.Folders.CountAsync(folder => folder.Name == "Replay Parent").ConfigureAwait(false));
        Assert.AreEqual(1, await database.Folders.CountAsync(folder => folder.Name == "Replay Parent/Child").ConfigureAwait(false));
        Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task NativeContactMutationsReplayResultsAndCreationReferencesWithoutDuplicatingData()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var operation = Guid.CreateVersion7();
        var original = await rig.InvokeAsync(rig.ContactsBatch(), operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.MutateAddressBooks, original.Invocations[0].Operation);
        Assert.AreEqual(MailOperationKind.MutateContacts, original.Invocations[1].Operation);
        Assert.IsNotNull(original.CreatedIds?["book"]);
        Assert.IsNotNull(original.CreatedIds?["card"]);
        var replay = await rig.InvokeAsync(rig.ContactsBatch(), operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(original.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(original.CreatedIds!.ToArray(), replay.CreatedIds!.ToArray());
        var database = rig.Context();
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(1, await database.DavCollections.CountAsync(book => book.DisplayName == "Replay book").ConfigureAwait(false));
        Assert.AreEqual(1, await database.DavCollections.CountAsync(book => book.IsDefault).ConfigureAwait(false));
        Assert.IsTrue(await database.DavCollections.Where(book => book.DisplayName == "Replay book")
            .Select(book => book.IsDefault).SingleAsync().ConfigureAwait(false));
        Assert.AreEqual(1, await database.DavResources.CountAsync(card => card.Uid == "replay-card-uid").ConfigureAwait(false));
        var receipts = await database.ApplicationOperationReceipts.OrderBy(receipt => receipt.OperationId).ToListAsync().ConfigureAwait(false);
        Assert.HasCount(2, receipts);
        foreach (var receipt in receipts)
        {
            Assert.AreEqual(LargeObjectProviders.AzureBlob, receipt.ObjectProvider, StringComparer.Ordinal);
            Assert.AreEqual(0, receipt.StepNumber);
            Assert.AreEqual("mail.operation", receipt.Purpose, StringComparer.Ordinal);
            Assert.IsTrue(receipt.PayloadLength > 0);
            var content = new MemoryStream();
            await using var contentLifetime = content.ConfigureAwait(false);
            await rig.Objects.CopyToAsync(Reference(receipt), content).ConfigureAwait(false);
            Assert.IsFalse(Encoding.UTF8.GetString(content.ToArray()).Contains("Replay book", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task ConcurrentWorkersWaitForTheDatabaseReceiptLockAndDoNotRepeatNativeMutations()
    {
        var pause = new ReceiptPause();
        var rig = (await Rig.CreateAsync(pause).ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var operation = Guid.CreateVersion7();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = rig.InvokeAsync(rig.ContactsBatch(), operation, timeout.Token);
        await pause.Entered.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        var second = rig.InvokeAsync(rig.ContactsBatch(), operation, timeout.Token);
        try
        {
            var waiting = rig.Source.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = @database AND wait_event = 'advisory')");
            await using var waitingLifetime = waiting.ConfigureAwait(false);
            waiting.Parameters.AddWithValue("database", rig.Database.DatabaseName);
            while (await waiting.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false) is not true)
                await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            Assert.IsFalse(second.IsCompleted);
        }
        finally
        {
            pause.Release.TrySetResult();
        }
        var results = await Task.WhenAll(first, second).WaitAsync(timeout.Token).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(results[0].Invocations), JsonSerializer.Serialize(results[1].Invocations), StringComparer.Ordinal);
        var database = rig.Context();
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(2, await database.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(1, await database.DavCollections.CountAsync(book => book.DisplayName == "Replay book").ConfigureAwait(false));
        Assert.AreEqual(1, await database.DavResources.CountAsync(card => card.Uid == "replay-card-uid").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CancellationBeforeReceiptCommitRollsBackBusinessRowsAndDeletesNewReceiptBlob()
    {
        using var cancellation = new CancellationTokenSource();
        var rig = (await Rig.CreateAsync(new CancelReceipt(cancellation)).ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.InvokeAsync(rig.ContactsBatch(), Guid.CreateVersion7(), cancellation.Token)).ConfigureAwait(false);
        var database = rig.Context();
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(0, await database.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await database.DavCollections.CountAsync(book => book.DisplayName == "Replay book").ConfigureAwait(false));
        var blobs = new List<string>();
        await foreach (var blob in rig.Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "application-receipts/", CancellationToken.None).ConfigureAwait(false))
            blobs.Add(blob.Name);
        Assert.HasCount(0, blobs);
    }

    [TestMethod]
    public async Task ConflictingInputOrAuthenticatedUserCannotReuseACommittedReceipt()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var operation = Guid.CreateVersion7();
        await rig.InvokeAsync(rig.ContactsBatch(), operation).ConfigureAwait(false);
        var changed = rig.ContactsBatch();
        changed.Invocations[0].Arguments["create"]!["book"]!["name"] = "Different book";
        var mismatch = await rig.InvokeAsync(changed, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.Failure, mismatch.Invocations[0].Operation);
        Assert.AreEqual("serverFail", mismatch.Invocations[0].Arguments["type"]!.GetValue<string>(), StringComparer.Ordinal);
        var foreignUser = new AuthenticatedMailUser(Guid.CreateVersion7(), "foreign@example.test");
        {
            var setup = rig.Context();
            await using var setupLifetime = setup.ConfigureAwait(false);
            await (setup.Users.AddAsync(new UserDB
            {
                Id = foreignUser.Id,
                Username = foreignUser.Username,
                PasswordHash = "unused",
                CompanyId = await setup.Users.Where(user => user.Id == rig.User.Id).Select(user => user.CompanyId).SingleAsync().ConfigureAwait(false)
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        using var scope = rig.Services.CreateScope();
        var foreign = await InvokeGatewayAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(),
            rig.ContactsBatch(), foreignUser, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.Failure, foreign.Invocations[0].Operation);
        var database = rig.Context();
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(2, await database.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await database.DavCollections.CountAsync(book => book.DisplayName == "Different book").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task MissingCommittedReceiptContentFailsClosedWithoutRepeatingBusinessMutations()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var operation = Guid.CreateVersion7();
        await rig.InvokeAsync(rig.ContactsBatch(), operation).ConfigureAwait(false);
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            var receipt = await database.ApplicationOperationReceipts.Where(row => row.OperationId == ProcessorGatewayJmapClient.ReplayOperationId(operation, 0)).SingleAsync().ConfigureAwait(false);
            Assert.IsTrue(await rig.Objects.DeleteIfMatchAsync(Reference(receipt)).ConfigureAwait(false));
        }
        var replay = await rig.InvokeAsync(rig.ContactsBatch(), operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.Failure, replay.Invocations[0].Operation);
        Assert.AreEqual("serverFail", replay.Invocations[0].Arguments["type"]!.GetValue<string>(), StringComparer.Ordinal);
        var unchanged = rig.Context();
        await using var unchangedLifetime = unchanged.ConfigureAwait(false);
        Assert.AreEqual(2, await unchanged.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(1, await unchanged.DavCollections.CountAsync(book => book.DisplayName == "Replay book").ConfigureAwait(false));
        Assert.AreEqual(1, await unchanged.DavResources.CountAsync(card => card.Uid == "replay-card-uid").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WorkerCrashBeforeResponsePublicationReclaimsTheSameRequestWithoutRepeatingMutations()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var mutationArguments = JsonSerializer.SerializeToNode(
            new MailAddressBookMutationCommand(rig.InboxId, true, null, false,
                new MailAddressBookTarget(null, "book"),
                [new MailAddressBookCreate("book", false,
                    new MailAddressBookValues("Replay book", null, 0, true))], [], []),
            SerializationOptions1)!.AsObject();
        var request = new ApplicationRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "jmap", ApplicationOperations.MailOperationExecute,
            "application/json", JsonSerializer.SerializeToUtf8Bytes(new MailOperationApplicationRequest(
                new ProtocolAuthentication(ProtocolAuthenticationKinds.Password, rig.User.Username, "test"),
                new([MailFeature.Basic, MailFeature.Contacts], MailOperationKind.MutateAddressBooks,
                    mutationArguments, GatewayJmapReferenceAliasCodec.Collect(mutationArguments))),
                SerializationOptions1), new Dictionary<string, string>(StringComparer.Ordinal), now, now.AddMinutes(3));
        await rig.Bus.EnqueueAsync(request).ConfigureAwait(false);
        var lease = await rig.Bus.TryClaimAsync("first-worker").ConfigureAwait(false);
        Assert.IsNotNull(lease);
        using var stop = new CancellationTokenSource();
        var crash = new CancelBeforePublication(rig.Bus, stop);
        using (var worker = new ApplicationRequestWorker(crash, rig.Services.GetRequiredService<IServiceScopeFactory>(),
                   new ApplicationWorkerIdentity("first-worker", TimeSpan.FromSeconds(1)), NullLogger<ApplicationRequestWorker>.Instance))
            await worker.ProcessLeaseAsync(lease, stop.Token).ConfigureAwait(false);
        {
            var status = rig.Source.CreateCommand("SELECT state FROM application_requests WHERE id = @id");
            await using var statusLifetime = status.ConfigureAwait(false);
            status.Parameters.AddWithValue("id", request.Id);
            Assert.AreEqual("processing", await status.ExecuteScalarAsync().ConfigureAwait(false));
        }
        {
            var expiration = rig.Source.CreateCommand("SELECT lease_expires_at FROM application_requests WHERE id = @id");
            await using var expirationLifetime = expiration.ConfigureAwait(false);
            expiration.Parameters.AddWithValue("id", request.Id);
            var delay = (DateTime)(await expiration.ExecuteScalarAsync().ConfigureAwait(false))! - DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
            if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
        }
        var retry = await rig.Bus.TryClaimAsync("replacement-worker").ConfigureAwait(false);
        Assert.IsNotNull(retry);
        using (var worker = new ApplicationRequestWorker(rig.Bus, rig.Services.GetRequiredService<IServiceScopeFactory>(),
                   new ApplicationWorkerIdentity("replacement-worker", TimeSpan.FromSeconds(1)), NullLogger<ApplicationRequestWorker>.Instance))
            await worker.ProcessLeaseAsync(retry, CancellationToken.None).ConfigureAwait(false);
        var response = await rig.Bus.WaitForResponseAsync(request.Id, request.Deadline).ConfigureAwait(false);
        Assert.IsFalse(response.IsError);
        using var document = JsonDocument.Parse(response.Payload);
        Assert.AreEqual((int)MailOperationKind.MutateAddressBooks, document.RootElement.GetProperty("operationResult").GetProperty("response").GetProperty("operation").GetInt32());
        var database = rig.Context();
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(1, await database.DavCollections.CountAsync(book => book.DisplayName == "Replay book").ConfigureAwait(false));
        Assert.AreEqual(0, await database.DavResources.CountAsync(card => card.Uid == "replay-card-uid").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task VerificationOutboxWakesWorkerAndRetriesTheSameEffectAfterLostEnqueueAcknowledgement()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var batch = new JmapApplicationBatch([MailFeature.Basic], [new JmapApplicationCall(MailOperationKind.MutateNotificationSubscriptions,
            new JsonObject { ["create"] = new JsonObject { ["device"] = new JsonObject
            { ["deviceClientId"] = "replay-device", ["url"] = "https://push.example.test/verification" } } }, "push")]);
        var operation = Guid.CreateVersion7();
        var initial = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.MutateNotificationSubscriptions, initial.Invocations[0].Operation);
        Assert.IsNotNull(initial.Invocations[0].Arguments["created"]?["device"]);
        Assert.IsTrue((await new WorkerWakeProbe(rig.Source).ReadAsync().ConfigureAwait(false)).HasDueWork);
        rig.Sink.FailAfterEnqueue = true;
        using (var scope = rig.Services.CreateScope())
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<ApplicationOperationReceiptStore>().DispatchNextEffectsAsync(CancellationToken.None).ConfigureAwait(false));
        {
            var count = rig.Source.CreateCommand("SELECT count(*) FROM presentation_requests");
            await using var countLifetime = count.ConfigureAwait(false);
            Assert.AreEqual(1L, await count.ExecuteScalarAsync().ConfigureAwait(false));
        }
        var delayed = await new WorkerWakeProbe(rig.Source).ReadAsync().ConfigureAwait(false);
        Assert.IsFalse(delayed.HasDueWork);
        Assert.IsTrue(delayed.NextDueAt > DateTimeOffset.UtcNow);
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.IsTrue((await database.ApplicationOperationReceipts.SingleAsync().ConfigureAwait(false)).EffectsPending);
            await database.ApplicationOperationReceipts.ExecuteUpdateAsync(update => update.SetProperty(receipt => receipt.EffectsRetryAt, DateTime.UtcNow.AddSeconds(-1))).ConfigureAwait(false);
        }
        rig.Sink.FailAfterEnqueue = false;
        using (var scope = rig.Services.CreateScope())
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<ApplicationOperationReceiptStore>().DispatchNextEffectsAsync(CancellationToken.None).ConfigureAwait(false));
        {
            var count = rig.Source.CreateCommand("SELECT count(*) FROM presentation_requests");
            await using var countLifetime = count.ConfigureAwait(false);
            Assert.AreEqual(1L, await count.ExecuteScalarAsync().ConfigureAwait(false));
        }
        {
            var database = rig.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.IsFalse((await database.ApplicationOperationReceipts.SingleAsync().ConfigureAwait(false)).EffectsPending);
            Assert.AreEqual(1, await database.JmapPushSubscriptions.CountAsync().ConfigureAwait(false));
        }
        var replay = await rig.InvokeAsync(batch, operation).ConfigureAwait(false);
        Assert.AreEqual(JsonSerializer.Serialize(initial.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
        Assert.IsFalse((await new WorkerWakeProbe(rig.Source).ReadAsync().ConfigureAwait(false)).HasDueWork);
    }

    private static LargeObjectReference Reference(ApplicationOperationReceiptDB row) =>
        new(row.ObjectProvider, row.ObjectName, row.PayloadLength, row.ObjectSha256, row.ObjectEntityTag);

    [TestMethod]
    public async Task BackupRestoreRebindsEncryptedReceiptsAndPreservesNativeReplay()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var operation = Guid.CreateVersion7();
        var original = await rig.InvokeAsync(rig.ContactsBatch(), operation).ConfigureAwait(false);
        var targetDatabase = (await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false)
            ?? throw new AssertFailedException("PostgreSQL is required."));
        await using var targetDatabaseLifetime = targetDatabase.ConfigureAwait(false);
        var targetSource = NpgsqlDataSource.Create(targetDatabase.ConnectionString);
        await using var targetSourceLifetime = targetSource.ConfigureAwait(false);
        var client = new BlobServiceClient(Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION"));
        var container = client.GetBlobContainerClient($"mk8-receipt-restored-{Guid.NewGuid():N}");
        var objects = new AzureBlobLargeObjectStore(client, new AzureBlobLargeObjectStoreOptions
        { ContainerName = container.Name, CreateContainerIfMissing = true });
        var temporary = Directory.CreateTempSubdirectory("mk8-receipt-backup-");
        try
        {
            var dump = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_PG_DUMP") ?? "pg_dump";
            var restore = Path.GetDirectoryName(dump) is { Length: > 0 } directory ? Path.Combine(directory, "pg_restore") : "pg_restore";
            var destination = Path.Combine(temporary.FullName, "snapshot");
            await DistributedBackupExporter.ExportAsync(rig.Source, rig.Objects, rig.Database.ConnectionString, destination, dump).ConfigureAwait(false);
            var manifest = await File.ReadAllLinesAsync(Path.Combine(destination, "references.jsonl")).ConfigureAwait(false);
            Assert.AreEqual(2, manifest.Select(line => JsonSerializer.Deserialize<DistributedBlobReferenceRow>(line))
                .Count(row => string.Equals(row?.Source, "application_operation_receipts.payload_object_name", StringComparison.Ordinal)));
            await DistributedBackupRestorer.RestoreAsync(destination, targetSource, targetDatabase.ConnectionString, objects, restore).ConfigureAwait(false);
            await DistributedRestoreActivationGuard.RequireReadyAsync(targetSource).ConfigureAwait(false);
            var services = rig.ServicesFor(targetDatabase.ConnectionString, objects);
            await using var servicesLifetime = services.ConfigureAwait(false);
            using var scope = services.CreateScope();
            var replay = await InvokeGatewayAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(),
                rig.ContactsBatch(), rig.User, operation).ConfigureAwait(false);
            Assert.AreEqual(JsonSerializer.Serialize(original.Invocations), JsonSerializer.Serialize(replay.Invocations), StringComparer.Ordinal);
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            Assert.AreEqual(2, await database.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(1, await database.DavResources.CountAsync(card => card.Uid == "replay-card-uid").ConfigureAwait(false));
            var originalDatabase = rig.Context();
            await using var originalDatabaseLifetime = originalDatabase.ConfigureAwait(false);
            Assert.AreNotEqual((await originalDatabase.ApplicationOperationReceipts.OrderBy(receipt => receipt.OperationId).FirstAsync().ConfigureAwait(false)).ObjectEntityTag,
                (await database.ApplicationOperationReceipts.OrderBy(receipt => receipt.OperationId).FirstAsync().ConfigureAwait(false)).ObjectEntityTag, StringComparer.Ordinal);
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
            temporary.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow(MailOperationKind.None, false, false)]
    [DataRow(MailOperationKind.None, true, false)]
    [DataRow((MailOperationKind)999, false, false)]
    [DataRow((MailOperationKind)999, true, false)]
    [DataRow(MailOperationKind.Failure, false, false)]
    [DataRow(MailOperationKind.Failure, true, false)]
    [DataRow(MailOperationKind.Failure, false, true)]
    [DataRow(MailOperationKind.Failure, true, true)]
    public async Task UnrenderablePrimaryOrAdditionalResultRollsBackBusinessWritesAndReceipt(
        MailOperationKind invalid, bool additional, bool legacy)
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        using var scope = rig.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var callbacks = 0;
        var handler = new MutatingResultMethod(database, invalid, additional, legacy, () => callbacks++);
        var processor = new JmapRequestProcessor([handler],
            scope.ServiceProvider.GetRequiredService<JmapAccountProfileService>(), database,
            scope.ServiceProvider.GetRequiredService<EnvironmentConfig>(),
            scope.ServiceProvider.GetRequiredService<LargeObjectTransactionEffects>(),
            NullLogger<JmapRequestProcessor>.Instance,
            scope.ServiceProvider.GetRequiredService<ApplicationOperationReceiptStore>());
        var result = await processor.ExecuteAsync(new MailOperationCommand(
            [MailFeature.Basic, MailFeature.Submission], MailOperationKind.MutateSubmissions,
            new JsonObject(), new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal)),
            rig.User, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.Failure, result.Response.Operation);
        var failure = GatewayMailOperationFailureCodec.Decode((JsonObject)ApplicationValueCodec.Decode(result.Response.Data)!);
        Assert.AreEqual(MailOperationFailureReason.InternalFailure, failure.Reason);
        Assert.IsNull(ApplicationValueCodec.Decode(result.Response.Data)!["type"]);
        Assert.IsFalse(result.KnownEntities.ContainsKey("transient"));
        Assert.AreEqual(0, callbacks);
        var restored = rig.Context();
        await using var restoredLifetime = restored.ConfigureAwait(false);
        Assert.AreEqual(rig.User.Username, (await restored.Users.SingleAsync().ConfigureAwait(false)).Username, StringComparer.Ordinal);
        Assert.AreEqual(0, await restored.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task NeutralPartialFailureCommitsAndReplaysWithoutRepeatingDomainEffects()
    {
        var rig = await Rig.CreateAsync().ConfigureAwait(false);
        await using var rigLifetime = rig.ConfigureAwait(false);
        using var scope = rig.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var callbacks = 0;
        var handler = new MutatingResultMethod(database, MailOperationKind.Failure, additional: false, legacy: false,
            () => callbacks++, MailOperationFailureReason.PartiallyCompleted);
        var processor = new JmapRequestProcessor([handler],
            scope.ServiceProvider.GetRequiredService<JmapAccountProfileService>(), database,
            scope.ServiceProvider.GetRequiredService<EnvironmentConfig>(),
            scope.ServiceProvider.GetRequiredService<LargeObjectTransactionEffects>(),
            NullLogger<JmapRequestProcessor>.Instance,
            scope.ServiceProvider.GetRequiredService<ApplicationOperationReceiptStore>());
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Submission],
            MailOperationKind.MutateSubmissions, new JsonObject(),
            new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal));
        var identity = Guid.CreateVersion7();
        var first = await processor.ExecuteAsync(command, rig.User, identity).ConfigureAwait(false);
        var replay = await processor.ExecuteAsync(command, rig.User, identity).ConfigureAwait(false);
        var data = (JsonObject)ApplicationValueCodec.Decode(replay.Response.Data)!;
        Assert.AreEqual(MailOperationFailureReason.PartiallyCompleted, GatewayMailOperationFailureCodec.Decode(data).Reason);
        Assert.IsFalse(data.ContainsKey("type"));
        Assert.IsTrue(JsonNode.DeepEquals(ApplicationValueCodec.Decode(first.Response.Data), data));
        Assert.AreEqual("object-id", replay.KnownEntities["transient"], StringComparer.Ordinal);
        Assert.AreEqual(1, callbacks);
        var restored = rig.Context();
        await using var restoredLifetime = restored.ConfigureAwait(false);
        Assert.AreEqual("changed@example.test", (await restored.Users.SingleAsync().ConfigureAwait(false)).Username, StringComparer.Ordinal);
        Assert.AreEqual(1, await restored.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    private static async Task<JmapApplicationBatchResult> InvokeGatewayAsync(
        JmapRequestProcessor processor, JmapApplicationBatch batch, AuthenticatedMailUser user,
        Guid operation, CancellationToken token = default)
    {
        processor.ValidatePlan(new(batch.Features, batch.Invocations.Length));
        var execution = await GatewayJmapBatchExecutor.ExecuteAsync(new ProcessorGatewayJmapClient(processor, user, operation),
            new(ProtocolAuthenticationKinds.Password, user.Username, "test"),
            batch, await processor.GetProfileAsync(user, token).ConfigureAwait(false), token).ConfigureAwait(false);
        return execution.Batch!;
    }

    [TestMethod]
    public async Task ReceiptPreservesRawValuesAndGatewayReplaysNormalizedReferences()
    {
        var rig = (await Rig.CreateAsync().ConfigureAwait(false));
        await using var rigLifetime = rig.ConfigureAwait(false);
        var identity = Guid.CreateVersion7();
        var command = new MailOperationCommand([MailFeature.Basic], MailOperationKind.FindFolders, new JsonObject(),
            new Dictionary<string, string>(StringComparer.Ordinal));
        using var scope = rig.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var receipts = scope.ServiceProvider.GetRequiredService<ApplicationOperationReceiptStore>();
        var calls = 0;
        var processor = new JmapRequestProcessor([new RawValueMethod(() => calls++)],
            scope.ServiceProvider.GetRequiredService<JmapAccountProfileService>(), database,
            scope.ServiceProvider.GetRequiredService<EnvironmentConfig>(),
            scope.ServiceProvider.GetRequiredService<LargeObjectTransactionEffects>(),
            NullLogger<JmapRequestProcessor>.Instance, receipts);
        var first = await processor.ExecuteAsync(command, rig.User, identity).ConfigureAwait(false);
        var raw = ApplicationValueCodec.Decode(first.Response.Data)!.AsObject();
        Assert.AreEqual("first", raw["\ud800"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("second", raw["\udfff"]!.GetValue<string>(), StringComparer.Ordinal);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(command, SerializationOptions1)));
        {
            var transaction = (await database.Database.BeginTransactionAsync().ConfigureAwait(false));
            await using var transactionLifetime = transaction.ConfigureAwait(false);
            var saved = await receipts.FindLockedAsync(new(identity, 0, rig.User.Id, "mail.operation", hash), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(saved);
            var receipt = JsonSerializer.Deserialize<JmapReplayState>(saved.Result.Span,
                SerializationOptions2)!;
            var persisted = ApplicationValueCodec.Decode(receipt.Response.Data)!.AsObject();
            CollectionAssert.AreEqual(raw.Select(member => member.Key).ToArray(), persisted.Select(member => member.Key).ToArray());
        }
        var replay = await processor.ExecuteAsync(command, rig.User, identity).ConfigureAwait(false);
        Assert.AreEqual(1, calls);
        var completed = new List<JmapApplicationInvocation> {
            new(MailOperationKind.FindFolders, GatewayJmapJson.SanitizeResponse(ApplicationValueCodec.Decode(replay.Response.Data)!.AsObject()), "source") };
        Assert.IsTrue(GatewayJmapArgumentBindingResolver.TryResolve(new(MailOperationKind.Echo, new JsonObject(), "target",
            [new("copied", "source", MailOperationKind.FindFolders, [new("\ufffd~2")])]),
            completed, out var selected, out _));
        Assert.AreEqual("second", selected["copied"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
    }

    private sealed class RawValueMethod(Action invoked) : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.FindFolders;
        public MailFeature Feature => MailFeature.Basic;
        public Task<JmapMethodResponse> InvokeAsync(JmapInvocationContext context, JsonObject arguments, CancellationToken cancellationToken)
        {
            invoked();
            return Task.FromResult(new JmapMethodResponse(Operation, new JsonObject
            {
                ["\ud800"] = "first",
                ["\udfff"] = "second",
                ["text"] = "\ufdd0"
            }));
        }
    }

    private sealed class MutatingResultMethod(EmailDbContext database, MailOperationKind invalid, bool additional, bool legacy,
        Action callback, MailOperationFailureReason? acceptedReason = null) : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.MutateSubmissions;
        public MailFeature Feature => MailFeature.Submission;
        public async Task<JmapMethodResponse> InvokeAsync(JmapInvocationContext context, JsonObject arguments,
            CancellationToken cancellationToken)
        {
            var user = await database.Users.SingleAsync(cancellationToken).ConfigureAwait(false);
            user.Username = "changed@example.test";
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            context.CreatedIds["transient"] = "object-id";
            context.AddPostCommitAction(_ => { callback(); return Task.CompletedTask; });
            var failureData = legacy ? new JsonObject { ["type"] = "serverPartialFail" } : new JsonObject { ["reason"] = 999 };
            var unsupported = acceptedReason is { } reason ? JmapMethodResponse.Failure(reason)
                : new JmapMethodResponse(invalid, invalid == MailOperationKind.Failure ? failureData : new JsonObject());
            return additional ? new JmapMethodResponse(Operation, new JsonObject(), [unsupported]) : unsupported;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly AesGcmPayloadProtector _protector;
        private Rig(PostgresTestDatabase database, NpgsqlDataSource source, BlobContainerClient container,
            AzureBlobLargeObjectStore objects, AesGcmPayloadProtector protector, ServiceProvider services,
            AuthenticatedMailUser user, Guid inboxId, PostgresApplicationBus bus, EffectSink sink)
        {
            Database = database; Source = source; Container = container; Objects = objects;
            _protector = protector; Services = services; User = user; InboxId = inboxId; Bus = bus; Sink = sink;
        }
        public PostgresTestDatabase Database { get; }
        public NpgsqlDataSource Source { get; }
        public BlobContainerClient Container { get; }
        public AzureBlobLargeObjectStore Objects { get; }
        public ServiceProvider Services { get; }
        public AuthenticatedMailUser User { get; }
        public Guid InboxId { get; }
        public PostgresApplicationBus Bus { get; }
        public EffectSink Sink { get; }

        public ServiceProvider ServicesFor(string connection, ILargeObjectStore objects) =>
            CreateServices(connection, objects, _protector, User, Sink);

        public static async Task<Rig> CreateAsync(IInterceptor? interceptor = null)
        {
            var connection = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
            if (string.IsNullOrWhiteSpace(connection)) Assert.Inconclusive("Azure Blob-compatible test configuration is required.");
            var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
            if (database is null) Assert.Inconclusive("PostgreSQL test configuration is required.");
            var source = NpgsqlDataSource.Create(database!.ConnectionString);
            var client = new BlobServiceClient(connection);
            var container = client.GetBlobContainerClient($"mk8-receipt-{Guid.NewGuid():N}");
            var objects = new AzureBlobLargeObjectStore(client, new AzureBlobLargeObjectStoreOptions { ContainerName = container.Name, CreateContainerIfMissing = true });
            var protector = AesGcmPayloadProtectorTests.CreateProtector("receipt", "durable-replay-key");
            var options = new PostgresMessagingOptions { LeaseDuration = TimeSpan.FromSeconds(5) };
            var bus = new PostgresApplicationBus(source, protector, options, largeObjectStore: objects);
            var sink = new EffectSink(new PostgresPresentationBus(source, protector, options, largeObjectStore: objects));
            var user = new AuthenticatedMailUser(Guid.CreateVersion7(), "replay@example.test");
            var provider = CreateServices(database.ConnectionString, objects, protector, user, sink, interceptor);
            var inboxId = Guid.CreateVersion7();
            try
            {
                using (var scope = provider.CreateScope())
                {
                    var context = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
                    await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
                    await new MailRuntimeSchemaService(context).EnsureAsync().ConfigureAwait(false);
                    var company = new CompanyDB { Id = Guid.CreateVersion7(), Name = "Replay fixture" };
                    var address = new AddressDB { Id = Guid.CreateVersion7(), Domain = "example.test", Company = company, IsActive = true };
                    var owner = new UserDB { Id = user.Id, Username = user.Username, PasswordHash = "unused", Company = company };
                    await context.Inboxes.AddAsync(new InboxDB { Id = inboxId, Name = "replay", Address = address, Owner = owner }).ConfigureAwait(false);
                    await context.SaveChangesAsync().ConfigureAwait(false);
                }
                await PostgresMessagingSchema.EnsureAsync(source).ConfigureAwait(false);
                return new Rig(database, source, container, objects, protector, provider, user, inboxId, bus, sink);
            }
            catch
            {
                await provider.DisposeAsync().ConfigureAwait(false);
                await container.DeleteIfExistsAsync().ConfigureAwait(false);
                protector.Dispose();
                await source.DisposeAsync().ConfigureAwait(false);
                await database.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static ServiceProvider CreateServices(string connection, ILargeObjectStore objects,
            AesGcmPayloadProtector protector, AuthenticatedMailUser user, EffectSink sink, IInterceptor? interceptor = null)
        {
            var environment = new EnvironmentConfig
            {
                Jmap = new JmapConfig { EnableJmap = true },
                Smtp = new SmtpConfig { Hostname = "email.example.test" }
            };
            var services = new ServiceCollection().AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Error))
                .AddSingleton(environment).AddSingleton<ILargeObjectStore>(objects)
                .AddSingleton<IStoredContentProtector>(new MessagingStoredContentProtector(protector))
                .AddSingleton<IDurablePresentationEffectSink>(sink).AddSingleton<IMailAuthenticator>(new Authenticator(user));
            services.AddDbContext<EmailDbContext>(builder =>
            {
                builder.UseNpgsql(connection);
                if (interceptor is not null) builder.AddInterceptors(interceptor);
            });
            services.AddJmapApplication();
            services.AddScoped<IApplicationRequestDispatcher, ApplicationRequestDispatcher>();
            services.AddSingleton<IJmapPushPresentationClient>(new SafePushClient(new JmapPushPresentationClient(
                new UnusedPresentationClient(), NullLogger<JmapPushPresentationClient>.Instance)));
            return services.BuildServiceProvider();
        }

        public EmailDbContext Context() => new(new DbContextOptionsBuilder<EmailDbContext>().UseNpgsql(Database.ConnectionString).Options);
        public async Task<JmapApplicationBatchResult> InvokeAsync(JmapApplicationBatch batch, Guid operation, CancellationToken token = default)
        {
            using var scope = Services.CreateScope();
            return await InvokeGatewayAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), batch, User, operation, token).ConfigureAwait(false);
        }
        public JmapApplicationBatch ContactsBatch() => new([MailFeature.Basic, MailFeature.Contacts],
        [
            new(MailOperationKind.MutateAddressBooks, new JsonObject { ["accountId"] = JmapId.Account(InboxId), ["create"] = new JsonObject
            { ["book"] = new JsonObject { ["name"] = "Replay book" } }, ["onSuccessSetIsDefault"] = "#book" }, "book"),
            new(MailOperationKind.MutateContacts, new JsonObject { ["accountId"] = JmapId.Account(InboxId), ["create"] = new JsonObject
            { ["card"] = new JsonObject { ["@type"] = "Card", ["version"] = "1.0", ["uid"] = "replay-card-uid", ["kind"] = "individual",
                ["name"] = new JsonObject { ["@type"] = "Name", ["full"] = "Replay person" }, ["addressBookIds"] = new JsonObject { ["#book"] = true } } } }, "card"),
        ], new Dictionary<string, string>(StringComparer.Ordinal));

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync().ConfigureAwait(false);
            await Container.DeleteIfExistsAsync().ConfigureAwait(false);
            _protector.Dispose();
            await Source.DisposeAsync().ConfigureAwait(false);
            await Database.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Authenticator(AuthenticatedMailUser user) : IMailAuthenticator
    {
        public Task<AuthenticatedMailUser?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthenticatedMailUser?>(user);
    }
    private sealed class UnusedPresentationClient : IPresentationRequestClient
    {
        public Task<ApplicationResponse> SendAsync(ApplicationRequest request, CancellationToken cancellationToken = default) => throw new AssertFailedException("The factory must not send presentation work.");
        public Task<ApplicationExchangeSnapshot?> GetAsync(Guid requestId, CancellationToken cancellationToken = default) => throw new AssertFailedException("The factory must not fetch presentation results.");
        public Task EnqueueAsync(ApplicationRequest request, CancellationToken cancellationToken = default) => throw new AssertFailedException("The factory must only build durable intent.");
        public Task<ApplicationResponse> WaitForResponseAsync(Guid requestId, DateTimeOffset deadline, CancellationToken cancellationToken = default) => throw new AssertFailedException("The factory must not wait for presentation.");
    }
    private sealed class SafePushClient(JmapPushPresentationClient factory) : IJmapPushPresentationClient
    {
        public Task<bool> IsSafeUrlAsync(string url, CancellationToken cancellationToken) => Task.FromResult(true);
        public ApplicationRequest? CreateVerificationRequest(string url, string? keysJson, DateTime expiresAt, JmapPushMessage payload) =>
            factory.CreateVerificationRequest(url, keysJson, expiresAt, payload);
        public Task EnqueueVerificationAsync(string url, string? keysJson, DateTime expiresAt, JmapPushMessage payload, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Relational verification must use its committed outbox intent.");
        public Task<WebPushSendOutcome> SendAsync(string url, string? keysJson, DateTime expiresAt, JmapPushMessage payload, CancellationToken cancellationToken) =>
            throw new AssertFailedException("This test does not send Web Push directly.");
    }
    private sealed class EffectSink(PostgresPresentationBus bus) : IDurablePresentationEffectSink
    {
        public bool FailAfterEnqueue { get; set; }
        public async Task EnqueueAsync(ApplicationRequest request, CancellationToken cancellationToken)
        {
            await bus.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
            if (FailAfterEnqueue) throw new TimeoutException("Simulated lost enqueue acknowledgement.");
        }
    }
    private sealed class ReceiptPause : SaveChangesInterceptor
    {
        private int _paused;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<ApplicationOperationReceiptDB>().Any(entry => entry.State == EntityState.Added)
                && Interlocked.CompareExchange(ref _paused, 1, 0) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }
    }
    private sealed class CancelReceipt(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<ApplicationOperationReceiptDB>().Any(entry => entry.State == EntityState.Added))
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                throw new OperationCanceledException(cancellation.Token);
            }
            return await ValueTask.FromResult(result).ConfigureAwait(false);
        }
    }
    private sealed class CancelBeforePublication(IApplicationRequestConsumer consumer, CancellationTokenSource stop) : IApplicationRequestConsumer
    {
        public Task<ApplicationRequestLease> WaitForRequestAsync(string workerId, CancellationToken cancellationToken = default) => consumer.WaitForRequestAsync(workerId, cancellationToken);
        public Task<ApplicationRequestLease?> TryClaimAsync(string workerId, CancellationToken cancellationToken = default) => consumer.TryClaimAsync(workerId, cancellationToken);
        public Task<bool> RenewLeaseAsync(ApplicationRequestLease lease, CancellationToken cancellationToken = default) => consumer.RenewLeaseAsync(lease, cancellationToken);
        public Task FailAsync(ApplicationRequestLease lease, string errorCode, string errorDetail, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Host cancellation must leave the original lease retryable.");
        public async Task CompleteAsync(ApplicationRequestLease lease, ApplicationResponse response, CancellationToken cancellationToken = default)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            throw new OperationCanceledException(stop.Token);
        }
    }
    private static readonly JsonSerializerOptions SerializationOptions1 = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions SerializationOptions2 = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 256 };
}
