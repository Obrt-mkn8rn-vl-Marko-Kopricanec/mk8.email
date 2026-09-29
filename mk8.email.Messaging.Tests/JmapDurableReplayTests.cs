using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class JmapDurableReplayTests
{
    [TestMethod]
    public async Task TypedBlobCopyUsesAzureStorageAndReplaysCommittedResultWithoutDuplicatingBlob()
    {
        await using var rig = await Rig.CreateAsync();
        var targetAccountId = Guid.CreateVersion7();
        await using (var setup = rig.Context())
        {
            var original = await setup.Inboxes.SingleAsync();
            setup.Inboxes.Add(new InboxDB
            {
                Id = targetAccountId,
                Name = "copy-target",
                AddressId = original.AddressId,
                OwnerId = rig.User.Id,
            });
            await setup.SaveChangesAsync();
        }
        string sourceBlobId;
        using (var scope = rig.Services.CreateScope())
        {
            var blobs = scope.ServiceProvider.GetRequiredService<JmapBlobService>();
            var source = await blobs.StoreAsync(rig.InboxId, "copied payload"u8.ToArray(),
                "text/plain", "payload.txt", CancellationToken.None);
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
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(MailOperationKind.CopyBinaryObjects, first.Invocations[0].Operation);
        var copiedBlobId = first.Invocations[0].Arguments["copied"]![sourceBlobId]!.GetValue<string>();
        Assert.AreEqual("notFound", first.Invocations[0].Arguments["notCopied"]!["Umissing"]!["type"]!
            .GetValue<string>());
        await using (var database = rig.Context())
        {
            Assert.AreEqual(2, await database.JmapBlobs.CountAsync());
            var copied = await database.JmapBlobs.SingleAsync(blob => blob.AccountId == targetAccountId);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, copied.ObjectProvider);
            Assert.IsNull(copied.Content);
        }
        using (var scope = rig.Services.CreateScope())
        {
            var blobs = scope.ServiceProvider.GetRequiredService<JmapBlobService>();
            var copied = await blobs.GetAsync(targetAccountId, copiedBlobId, CancellationToken.None);
            Assert.IsNotNull(copied);
            CollectionAssert.AreEqual("copied payload"u8.ToArray(), copied.Content);
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        await using (var database = rig.Context())
            Assert.AreEqual(2, await database.JmapBlobs.CountAsync());
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.AreNotEqual(copiedBlobId, fresh.Invocations[0].Arguments["copied"]![sourceBlobId]!.GetValue<string>());
        await using var verification = rig.Context();
        Assert.AreEqual(3, await verification.JmapBlobs.CountAsync());
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync());
    }

    [TestMethod]
    public async Task TypedSubmissionReadReplaysCommittedSnapshotAcrossWorkerRetries()
    {
        await using var rig = await Rig.CreateAsync();
        var submissionId = Guid.CreateVersion7();
        await using (var setup = rig.Context())
        {
            setup.JmapEmailSubmissions.Add(new JmapEmailSubmissionDB
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
            });
            await setup.SaveChangesAsync();
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.ReadSubmissions, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["ids"] = new JsonArray(JmapId.Submission(submissionId)),
                ["properties"] = new JsonArray("undoStatus", "envelope"),
            }, "submission")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual("final", first.Invocations[0].Arguments["list"]![0]!["undoStatus"]!.GetValue<string>());
        Assert.AreEqual(rig.User.Username,
            first.Invocations[0].Arguments["list"]![0]!["envelope"]!["mailFrom"]!["email"]!.GetValue<string>());
        await using (var changed = rig.Context())
        {
            var submission = await changed.JmapEmailSubmissions.SingleAsync();
            submission.UndoStatus = "pending";
            submission.EnvelopeSender = "updated@example.test";
            await changed.SaveChangesAsync();
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.AreEqual("pending", fresh.Invocations[0].Arguments["list"]![0]!["undoStatus"]!.GetValue<string>());
        Assert.AreEqual("updated@example.test",
            fresh.Invocations[0].Arguments["list"]![0]!["envelope"]!["mailFrom"]!["email"]!.GetValue<string>());
        await using var verification = rig.Context();
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync());
    }

    [TestMethod]
    public async Task TypedSubmissionQueryReplaysCommittedFilteredOrderAcrossWorkerRetries()
    {
        await using var rig = await Rig.CreateAsync();
        var submissionId = Guid.CreateVersion7();
        await using (var setup = rig.Context())
        {
            setup.JmapEmailSubmissions.Add(NewSubmission(rig, submissionId, "final"));
            await setup.SaveChangesAsync();
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.FindSubmissions, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["filter"] = new JsonObject { ["undoStatus"] = "final" },
                ["calculateTotal"] = true,
            }, "query")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(MailOperationKind.FindSubmissions, first.Invocations[0].Operation);
        Assert.AreEqual(JmapId.Submission(submissionId), first.Invocations[0].Arguments["ids"]![0]!.GetValue<string>());
        await using (var changed = rig.Context())
        {
            var submission = await changed.JmapEmailSubmissions.SingleAsync();
            submission.UndoStatus = "canceled";
            await changed.SaveChangesAsync();
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.AreEqual(0, fresh.Invocations[0].Arguments["ids"]!.AsArray().Count);
        Assert.AreEqual(0, fresh.Invocations[0].Arguments["total"]!.GetValue<int>());
        await using var verification = rig.Context();
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync());
    }

    [TestMethod]
    public async Task TypedSubmissionQueryDoesNotMatchOpaqueAnchorToLegacyEmptyGuidRow()
    {
        await using var rig = await Rig.CreateAsync();
        await using (var setup = rig.Context())
        {
            var legacyRow = NewSubmission(rig, Guid.Empty, "final");
            setup.JmapEmailSubmissions.Add(legacyRow);
            await setup.SaveChangesAsync();
            // EF generates a new key for Guid.Empty, so create the legacy row directly in PostgreSQL.
            await setup.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE jmap_email_submissions SET id = {Guid.Empty} WHERE id = {legacyRow.Id}");
        }
        var arguments = new JsonObject
        {
            ["accountId"] = JmapId.Account(rig.InboxId),
            ["anchor"] = JmapId.Submission(Guid.Empty),
        };
        var canonical = await rig.InvokeAsync(new JmapApplicationBatch(
            [MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.FindSubmissions, (JsonObject)arguments.DeepClone(), "canonical")]),
            Guid.CreateVersion7());
        Assert.IsNotNull(canonical.Invocations[0].Arguments["ids"],
            canonical.Invocations[0].Arguments.ToJsonString());
        Assert.AreEqual(JmapId.Submission(Guid.Empty),
            canonical.Invocations[0].Arguments["ids"]![0]!.GetValue<string>());
        arguments["anchor"] = "opaque";
        var opaque = await rig.InvokeAsync(new JmapApplicationBatch(
            [MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.FindSubmissions, arguments, "opaque")]),
            Guid.CreateVersion7());
        Assert.AreEqual("anchorNotFound", opaque.Invocations[0].Arguments["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task TypedSubmissionQueryChangesReplaysCommittedDeltaAcrossWorkerRetries()
    {
        await using var rig = await Rig.CreateAsync();
        var query = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.FindSubmissions, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
            }, "initial")]);
        var initial = await rig.InvokeAsync(query, Guid.CreateVersion7());
        var since = initial.Invocations[0].Arguments["queryState"]!.GetValue<string>();
        var submissionId = Guid.CreateVersion7();
        await using (var setup = rig.Context())
        {
            setup.JmapEmailSubmissions.Add(NewSubmission(rig, submissionId, "final"));
            await setup.SaveChangesAsync();
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
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(MailOperationKind.FindSubmissionChanges, first.Invocations[0].Operation);
        Assert.AreEqual(JmapId.Submission(submissionId),
            first.Invocations[0].Arguments["added"]![0]!["id"]!.GetValue<string>());
        await using (var changed = rig.Context())
        {
            var submission = await changed.JmapEmailSubmissions.SingleAsync();
            submission.UndoStatus = "canceled";
            await changed.SaveChangesAsync();
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.AreEqual(0, fresh.Invocations[0].Arguments["added"]!.AsArray().Count);
        await using var verification = rig.Context();
        Assert.AreEqual(3, await verification.ApplicationOperationReceipts.CountAsync());
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
        await using var rig = await Rig.CreateAsync();
        var folderId = Guid.CreateVersion7();
        var emailId = Guid.CreateVersion7();
        await using (var setup = rig.Context())
        {
            setup.Folders.Add(new FolderDB
            {
                Id = folderId,
                InboxId = rig.InboxId,
                Name = "Inbox",
                UidValidity = 1,
                NextUid = 2,
            });
            setup.Emails.Add(new EmailDB
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
            });
            await setup.SaveChangesAsync();
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.ReadThreads, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["properties"] = new JsonArray("emailIds"),
            }, "thread")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual("TYyE", first.Invocations[0].Arguments["list"]![0]!["id"]!.GetValue<string>());
        Assert.AreEqual(JmapId.Email(emailId),
            first.Invocations[0].Arguments["list"]![0]!["emailIds"]![0]!.GetValue<string>());
        await using (var changed = rig.Context())
        {
            var email = await changed.Emails.SingleAsync();
            email.ThreadObjectId = "changed";
            await changed.SaveChangesAsync();
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.AreEqual("Tchanged", fresh.Invocations[0].Arguments["list"]![0]!["id"]!.GetValue<string>());
        await using var verification = rig.Context();
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync());
    }

    [TestMethod]
    public async Task TypedVacationReadReplaysItsCommittedSnapshotAcrossWorkerRetries()
    {
        await using var rig = await Rig.CreateAsync();
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.AutomaticReplies],
            [new JmapApplicationCall(MailOperationKind.ReadVacationSettings, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["properties"] = new JsonArray("subject"),
            }, "vacation")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(MailOperationKind.ReadVacationSettings, first.Invocations[0].Operation);
        Assert.IsNull(first.Invocations[0].Arguments["list"]![0]!["subject"]);
        await using (var changed = rig.Context())
        {
            var vacation = await changed.JmapVacationResponses.SingleAsync();
            vacation.Subject = "New subject";
            await changed.SaveChangesAsync();
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.AreEqual("New subject", fresh.Invocations[0].Arguments["list"]![0]!["subject"]!.GetValue<string>());
        await using var verification = rig.Context();
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync());
    }

    [TestMethod]
    public async Task TypedVacationMutationUsesAzureBlobAndReplaysWithoutRepeatingChanges()
    {
        await using var rig = await Rig.CreateAsync();
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
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(MailOperationKind.MutateVacationSettings, first.Invocations[0].Operation);
        Assert.IsTrue(first.Invocations[0].Arguments["updated"]!.AsObject().ContainsKey("singleton"));
        string? originalBodyObject;
        await using (var database = rig.Context())
        {
            var vacation = await database.JmapVacationResponses.SingleAsync();
            Assert.AreEqual("Away", vacation.Subject);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, vacation.BodyObjectProvider);
            Assert.IsNull(vacation.TextBody);
            Assert.IsNull(vacation.HtmlBody);
            originalBodyObject = vacation.BodyObjectName;
            vacation.Subject = "Changed after commit";
            await database.SaveChangesAsync();
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        await using (var database = rig.Context())
        {
            var vacation = await database.JmapVacationResponses.SingleAsync();
            Assert.AreEqual("Changed after commit", vacation.Subject);
            Assert.AreEqual(originalBodyObject, vacation.BodyObjectName);
        }
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.IsTrue(fresh.Invocations[0].Arguments["updated"]!.AsObject().ContainsKey("singleton"));
        await using var verification = rig.Context();
        Assert.AreEqual("Away", (await verification.JmapVacationResponses.SingleAsync()).Subject);
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync());
    }

    [TestMethod]
    public async Task TypedPushReadReplaysItsCommittedSnapshotAcrossWorkerRetries()
    {
        await using var rig = await Rig.CreateAsync();
        var subscriptionId = Guid.CreateVersion7();
        await using (var setup = rig.Context())
        {
            setup.JmapPushSubscriptions.Add(new JmapPushSubscriptionDB
            {
                Id = subscriptionId,
                SubscriptionObjectId = JmapId.PushSubscription(subscriptionId),
                UserId = rig.User.Id,
                DeviceClientId = "initial",
                Url = "https://push.example.test/",
                VerificationCode = "private-code",
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            });
            await setup.SaveChangesAsync();
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic],
            [new JmapApplicationCall(MailOperationKind.ReadNotificationSubscriptions, new JsonObject
            {
                ["properties"] = new JsonArray("deviceClientId"),
            }, "push")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(MailOperationKind.ReadNotificationSubscriptions, first.Invocations[0].Operation);
        Assert.AreEqual("initial", first.Invocations[0].Arguments["list"]![0]!["deviceClientId"]!.GetValue<string>());
        await using (var changed = rig.Context())
        {
            var subscription = await changed.JmapPushSubscriptions.SingleAsync();
            subscription.DeviceClientId = "updated";
            await changed.SaveChangesAsync();
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.AreEqual("updated", fresh.Invocations[0].Arguments["list"]![0]!["deviceClientId"]!.GetValue<string>());
        await using var verification = rig.Context();
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync());
    }

    [TestMethod]
    public async Task TypedIdentityReadReplaysItsCommittedSnapshotAcrossWorkerRetries()
    {
        await using var rig = await Rig.CreateAsync();
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Submission],
            [new JmapApplicationCall(MailOperationKind.ReadSenderIdentities, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["properties"] = new JsonArray("name", "email"),
            }, "identities")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(MailOperationKind.ReadSenderIdentities, first.Invocations[0].Operation);
        Assert.AreEqual(string.Empty, first.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>());
        await using (var changed = rig.Context())
        {
            var identity = await changed.JmapIdentities.SingleAsync();
            identity.Name = "Renamed sender";
            await changed.SaveChangesAsync();
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.AreEqual("Renamed sender", fresh.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>());
        await using var verification = rig.Context();
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync());
    }

    [TestMethod]
    public async Task TypedAddressBookReadReplaysItsCommittedSnapshotAcrossWorkerRetries()
    {
        await using var rig = await Rig.CreateAsync();
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Contacts],
            [new JmapApplicationCall(MailOperationKind.ReadAddressBooks, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["properties"] = new JsonArray("name"),
            }, "books")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(MailOperationKind.ReadAddressBooks, first.Invocations[0].Operation);
        Assert.AreEqual("Address Book", first.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>());
        await using (var changed = rig.Context())
        {
            var book = await changed.DavCollections.SingleAsync();
            book.DisplayName = "Renamed book";
            await changed.SaveChangesAsync();
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.AreEqual("Renamed book", fresh.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>());
        await using var verification = rig.Context();
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync());
    }

    [TestMethod]
    public async Task PostgreSqlChangeReaderPreservesContactAndIdentityLazyDefaults()
    {
        await using var rig = await Rig.CreateAsync();
        using var scope = rig.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IMailChangesReader>();
        await using var before = rig.Context();
        Assert.AreEqual(0, await before.DavCollections.CountAsync());
        Assert.AreEqual(0, await before.JmapIdentities.CountAsync());

        var denied = await reader.ReadAsync(MailOperationKind.ReadAddressBookChanges,
            new MailChangesCommand(rig.InboxId, "s0", null, false), rig.User, CancellationToken.None);
        Assert.AreEqual(MailChangesStatus.AccountNotSupported, denied.Status);
        await using var unchanged = rig.Context();
        Assert.AreEqual(0, await unchanged.DavCollections.CountAsync());

        var books = await reader.ReadAsync(MailOperationKind.ReadAddressBookChanges,
            new MailChangesCommand(rig.InboxId, "s0", null, true), rig.User, CancellationToken.None);
        Assert.AreEqual(MailChangesStatus.Ok, books.Status);
        Assert.IsTrue(books.CreatedKeys.Any(key => key.StartsWith('D')));
        var identities = await reader.ReadAsync(MailOperationKind.ReadSenderIdentityChanges,
            new MailChangesCommand(rig.InboxId, "s0", null, true), rig.User, CancellationToken.None);
        Assert.AreEqual(MailChangesStatus.Ok, identities.Status);
        Assert.IsTrue(identities.CreatedKeys.Contains(JmapId.Identity(rig.InboxId)));
        await using var after = rig.Context();
        Assert.AreEqual(1, await after.DavCollections.CountAsync());
        Assert.AreEqual(1, await after.JmapIdentities.CountAsync());
    }

    [TestMethod]
    public async Task TypedMailboxChangesReplaysItsCommittedStateAcrossWorkerRetries()
    {
        await using var rig = await Rig.CreateAsync();
        var folderId = Guid.CreateVersion7();
        await using (var setup = rig.Context())
        {
            setup.Folders.Add(new FolderDB
            {
                Id = folderId,
                InboxId = rig.InboxId,
                Name = "Initial",
                UidValidity = 1,
                NextUid = 1,
            });
            await setup.SaveChangesAsync();
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.ReadFolderChanges, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["sinceState"] = "s0",
            }, "changes")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(MailOperationKind.ReadFolderChanges, first.Invocations[0].Operation);
        Assert.IsTrue(first.Invocations[0].Arguments["created"]!.AsArray()
            .Any(value => value!.GetValue<string>() == JmapId.Mailbox(folderId)));
        await using (var changed = rig.Context())
        {
            var folder = await changed.Folders.SingleAsync(row => row.Id == folderId);
            folder.Name = "Updated";
            await changed.SaveChangesAsync();
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.AreNotEqual(first.Invocations[0].Arguments["newState"]!.GetValue<string>(),
            fresh.Invocations[0].Arguments["newState"]!.GetValue<string>());
        await using var verification = rig.Context();
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync());
    }

    [TestMethod]
    public async Task TypedMailboxReadReplaysItsCommittedSnapshotAcrossWorkerRetries()
    {
        await using var rig = await Rig.CreateAsync();
        var folderId = Guid.CreateVersion7();
        await using (var setup = rig.Context())
        {
            setup.Folders.Add(new FolderDB
            {
                Id = folderId,
                InboxId = rig.InboxId,
                Name = "Before retry",
                UidValidity = 1,
                NextUid = 1,
            });
            await setup.SaveChangesAsync();
        }
        var batch = new JmapApplicationBatch([MailFeature.Basic, MailFeature.Messages],
            [new JmapApplicationCall(MailOperationKind.ReadFolders, new JsonObject
            {
                ["accountId"] = JmapId.Account(rig.InboxId),
                ["ids"] = new JsonArray(JmapId.Mailbox(folderId)),
                ["properties"] = new JsonArray("name"),
            }, "get")]);
        var operation = Guid.CreateVersion7();
        var first = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual("Before retry", first.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>());
        await using (var changed = rig.Context())
        {
            var folder = await changed.Folders.SingleAsync(row => row.Id == folderId);
            folder.Name = "After retry";
            await changed.SaveChangesAsync();
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(first.Invocations), JsonSerializer.Serialize(replay.Invocations));
        var fresh = await rig.InvokeAsync(batch, Guid.CreateVersion7());
        Assert.AreEqual("After retry", fresh.Invocations[0].Arguments["list"]![0]!["name"]!.GetValue<string>());
        await using var verification = rig.Context();
        Assert.AreEqual(2, await verification.ApplicationOperationReceipts.CountAsync());
    }

    [TestMethod]
    public async Task NativeContactMutationsReplayResultsAndCreationReferencesWithoutDuplicatingData()
    {
        await using var rig = await Rig.CreateAsync();
        var operation = Guid.CreateVersion7();
        var original = await rig.InvokeAsync(rig.ContactsBatch(), operation);
        Assert.AreEqual(MailOperationKind.MutateAddressBooks, original.Invocations[0].Operation);
        Assert.AreEqual(MailOperationKind.MutateContacts, original.Invocations[1].Operation);
        Assert.IsNotNull(original.CreatedIds?["book"]);
        Assert.IsNotNull(original.CreatedIds?["card"]);
        var replay = await rig.InvokeAsync(rig.ContactsBatch(), operation);
        Assert.AreEqual(JsonSerializer.Serialize(original.Invocations), JsonSerializer.Serialize(replay.Invocations));
        CollectionAssert.AreEquivalent(original.CreatedIds!.ToArray(), replay.CreatedIds!.ToArray());
        await using var database = rig.Context();
        Assert.AreEqual(1, await database.DavCollections.CountAsync(book => book.DisplayName == "Replay book"));
        Assert.AreEqual(1, await database.DavResources.CountAsync(card => card.Uid == "replay-card-uid"));
        var receipts = await database.ApplicationOperationReceipts.OrderBy(receipt => receipt.OperationId).ToListAsync();
        Assert.HasCount(2, receipts);
        foreach (var receipt in receipts)
        {
            Assert.AreEqual(LargeObjectProviders.AzureBlob, receipt.ObjectProvider);
            Assert.AreEqual(0, receipt.StepNumber);
            Assert.AreEqual("mail.operation", receipt.Purpose);
            Assert.IsTrue(receipt.PayloadLength > 0);
            await using var content = new MemoryStream();
            await rig.Objects.CopyToAsync(Reference(receipt), content);
            Assert.IsFalse(Encoding.UTF8.GetString(content.ToArray()).Contains("Replay book", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task ConcurrentWorkersWaitForTheDatabaseReceiptLockAndDoNotRepeatNativeMutations()
    {
        var pause = new ReceiptPause();
        await using var rig = await Rig.CreateAsync(pause);
        var operation = Guid.CreateVersion7();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = rig.InvokeAsync(rig.ContactsBatch(), operation, timeout.Token);
        await pause.Entered.Task.WaitAsync(timeout.Token);
        var second = rig.InvokeAsync(rig.ContactsBatch(), operation, timeout.Token);
        try
        {
            await using var waiting = rig.Source.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = @database AND wait_event = 'advisory')");
            waiting.Parameters.AddWithValue("database", rig.Database.DatabaseName);
            while (await waiting.ExecuteScalarAsync(timeout.Token) is not true)
                await Task.Delay(25, timeout.Token);
            Assert.IsFalse(second.IsCompleted);
        }
        finally
        {
            pause.Release.TrySetResult();
        }
        var results = await Task.WhenAll(first, second).WaitAsync(timeout.Token);
        Assert.AreEqual(JsonSerializer.Serialize(results[0].Invocations), JsonSerializer.Serialize(results[1].Invocations));
        await using var database = rig.Context();
        Assert.AreEqual(2, await database.ApplicationOperationReceipts.CountAsync());
        Assert.AreEqual(1, await database.DavCollections.CountAsync(book => book.DisplayName == "Replay book"));
        Assert.AreEqual(1, await database.DavResources.CountAsync(card => card.Uid == "replay-card-uid"));
    }

    [TestMethod]
    public async Task CancellationBeforeReceiptCommitRollsBackBusinessRowsAndDeletesNewReceiptBlob()
    {
        using var cancellation = new CancellationTokenSource();
        await using var rig = await Rig.CreateAsync(new CancelReceipt(cancellation));
        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.InvokeAsync(rig.ContactsBatch(), Guid.CreateVersion7(), cancellation.Token));
        await using var database = rig.Context();
        Assert.AreEqual(0, await database.ApplicationOperationReceipts.CountAsync());
        Assert.AreEqual(0, await database.DavCollections.CountAsync(book => book.DisplayName == "Replay book"));
        var blobs = new List<string>();
        await foreach (var blob in rig.Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "application-receipts/", CancellationToken.None))
            blobs.Add(blob.Name);
        Assert.HasCount(0, blobs);
    }

    [TestMethod]
    public async Task ConflictingInputOrAuthenticatedUserCannotReuseACommittedReceipt()
    {
        await using var rig = await Rig.CreateAsync();
        var operation = Guid.CreateVersion7();
        await rig.InvokeAsync(rig.ContactsBatch(), operation);
        var changed = rig.ContactsBatch();
        changed.Invocations[0].Arguments["create"]!["book"]!["name"] = "Different book";
        var mismatch = await rig.InvokeAsync(changed, operation);
        Assert.AreEqual(MailOperationKind.Failure, mismatch.Invocations[0].Operation);
        Assert.AreEqual("serverFail", mismatch.Invocations[0].Arguments["type"]!.GetValue<string>());
        var foreignUser = new AuthenticatedMailUser(Guid.CreateVersion7(), "foreign@example.test");
        await using (var setup = rig.Context())
        {
            setup.Users.Add(new UserDB
            {
                Id = foreignUser.Id,
                Username = foreignUser.Username,
                PasswordHash = "unused",
                CompanyId = await setup.Users.Where(user => user.Id == rig.User.Id).Select(user => user.CompanyId).SingleAsync()
            });
            await setup.SaveChangesAsync();
        }
        using var scope = rig.Services.CreateScope();
        var foreign = await InvokeGatewayAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(),
            rig.ContactsBatch(), foreignUser, operation);
        Assert.AreEqual(MailOperationKind.Failure, foreign.Invocations[0].Operation);
        await using var database = rig.Context();
        Assert.AreEqual(2, await database.ApplicationOperationReceipts.CountAsync());
        Assert.AreEqual(0, await database.DavCollections.CountAsync(book => book.DisplayName == "Different book"));
    }

    [TestMethod]
    public async Task MissingCommittedReceiptContentFailsClosedWithoutRepeatingBusinessMutations()
    {
        await using var rig = await Rig.CreateAsync();
        var operation = Guid.CreateVersion7();
        await rig.InvokeAsync(rig.ContactsBatch(), operation);
        await using (var database = rig.Context())
        {
            var receipt = await database.ApplicationOperationReceipts.Where(row => row.OperationId == ProcessorGatewayJmapClient.ReplayOperationId(operation, 0)).SingleAsync();
            Assert.IsTrue(await rig.Objects.DeleteIfMatchAsync(Reference(receipt)));
        }
        var replay = await rig.InvokeAsync(rig.ContactsBatch(), operation);
        Assert.AreEqual(MailOperationKind.Failure, replay.Invocations[0].Operation);
        Assert.AreEqual("serverFail", replay.Invocations[0].Arguments["type"]!.GetValue<string>());
        await using var unchanged = rig.Context();
        Assert.AreEqual(2, await unchanged.ApplicationOperationReceipts.CountAsync());
        Assert.AreEqual(1, await unchanged.DavCollections.CountAsync(book => book.DisplayName == "Replay book"));
        Assert.AreEqual(1, await unchanged.DavResources.CountAsync(card => card.Uid == "replay-card-uid"));
    }

    [TestMethod]
    public async Task WorkerCrashBeforeResponsePublicationReclaimsTheSameRequestWithoutRepeatingMutations()
    {
        await using var rig = await Rig.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var mutationArguments = rig.ContactsBatch().Invocations[0].Arguments;
        var request = new ApplicationRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "jmap", ApplicationOperations.MailOperationExecute,
            "application/json", JsonSerializer.SerializeToUtf8Bytes(new MailOperationApplicationRequest(
                new ProtocolAuthentication(ProtocolAuthenticationKinds.Password, rig.User.Username, "test"),
                new([MailFeature.Basic, MailFeature.Contacts], MailOperationKind.MutateAddressBooks,
                    mutationArguments, GatewayJmapReferenceAliasCodec.Collect(mutationArguments))),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), new Dictionary<string, string>(), now, now.AddMinutes(3));
        await rig.Bus.EnqueueAsync(request);
        var lease = await rig.Bus.TryClaimAsync("first-worker");
        Assert.IsNotNull(lease);
        using var stop = new CancellationTokenSource();
        var crash = new CancelBeforePublication(rig.Bus, stop);
        using (var worker = new ApplicationRequestWorker(crash, rig.Services.GetRequiredService<IServiceScopeFactory>(),
                   new ApplicationWorkerIdentity("first-worker", TimeSpan.FromSeconds(1)), NullLogger<ApplicationRequestWorker>.Instance))
            await worker.ProcessLeaseAsync(lease, stop.Token);
        await using (var status = rig.Source.CreateCommand("SELECT state FROM application_requests WHERE id = @id"))
        {
            status.Parameters.AddWithValue("id", request.Id);
            Assert.AreEqual("processing", await status.ExecuteScalarAsync());
        }
        await using (var expiration = rig.Source.CreateCommand("SELECT lease_expires_at FROM application_requests WHERE id = @id"))
        {
            expiration.Parameters.AddWithValue("id", request.Id);
            var delay = (DateTime)(await expiration.ExecuteScalarAsync())! - DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
        }
        var retry = await rig.Bus.TryClaimAsync("replacement-worker");
        Assert.IsNotNull(retry);
        using (var worker = new ApplicationRequestWorker(rig.Bus, rig.Services.GetRequiredService<IServiceScopeFactory>(),
                   new ApplicationWorkerIdentity("replacement-worker", TimeSpan.FromSeconds(1)), NullLogger<ApplicationRequestWorker>.Instance))
            await worker.ProcessLeaseAsync(retry, CancellationToken.None);
        var response = await rig.Bus.WaitForResponseAsync(request.Id, request.Deadline);
        Assert.IsFalse(response.IsError);
        using var document = JsonDocument.Parse(response.Payload);
        Assert.AreEqual((int)MailOperationKind.MutateAddressBooks, document.RootElement.GetProperty("operationResult").GetProperty("response").GetProperty("operation").GetInt32());
        await using var database = rig.Context();
        Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync());
        Assert.AreEqual(1, await database.DavCollections.CountAsync(book => book.DisplayName == "Replay book"));
        Assert.AreEqual(0, await database.DavResources.CountAsync(card => card.Uid == "replay-card-uid"));
    }

    [TestMethod]
    public async Task VerificationOutboxWakesWorkerAndRetriesTheSameEffectAfterLostEnqueueAcknowledgement()
    {
        await using var rig = await Rig.CreateAsync();
        var batch = new JmapApplicationBatch([MailFeature.Basic], [new JmapApplicationCall(MailOperationKind.MutateNotificationSubscriptions,
            new JsonObject { ["create"] = new JsonObject { ["device"] = new JsonObject
            { ["deviceClientId"] = "replay-device", ["url"] = "https://push.example.test/verification" } } }, "push")]);
        var operation = Guid.CreateVersion7();
        var initial = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(MailOperationKind.MutateNotificationSubscriptions, initial.Invocations[0].Operation);
        Assert.IsNotNull(initial.Invocations[0].Arguments["created"]?["device"]);
        Assert.IsTrue((await new WorkerWakeProbe(rig.Source).ReadAsync()).HasDueWork);
        rig.Sink.FailAfterEnqueue = true;
        using (var scope = rig.Services.CreateScope())
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<ApplicationOperationReceiptStore>().DispatchNextEffectsAsync(CancellationToken.None));
        await using (var count = rig.Source.CreateCommand("SELECT count(*) FROM presentation_requests"))
            Assert.AreEqual(1L, await count.ExecuteScalarAsync());
        var delayed = await new WorkerWakeProbe(rig.Source).ReadAsync();
        Assert.IsFalse(delayed.HasDueWork);
        Assert.IsTrue(delayed.NextDueAt > DateTimeOffset.UtcNow);
        await using (var database = rig.Context())
        {
            Assert.IsTrue((await database.ApplicationOperationReceipts.SingleAsync()).EffectsPending);
            await database.ApplicationOperationReceipts.ExecuteUpdateAsync(update => update.SetProperty(receipt => receipt.EffectsRetryAt, DateTime.UtcNow.AddSeconds(-1)));
        }
        rig.Sink.FailAfterEnqueue = false;
        using (var scope = rig.Services.CreateScope())
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<ApplicationOperationReceiptStore>().DispatchNextEffectsAsync(CancellationToken.None));
        await using (var count = rig.Source.CreateCommand("SELECT count(*) FROM presentation_requests"))
            Assert.AreEqual(1L, await count.ExecuteScalarAsync());
        await using (var database = rig.Context())
        {
            Assert.IsFalse((await database.ApplicationOperationReceipts.SingleAsync()).EffectsPending);
            Assert.AreEqual(1, await database.JmapPushSubscriptions.CountAsync());
        }
        var replay = await rig.InvokeAsync(batch, operation);
        Assert.AreEqual(JsonSerializer.Serialize(initial.Invocations), JsonSerializer.Serialize(replay.Invocations));
        Assert.IsFalse((await new WorkerWakeProbe(rig.Source).ReadAsync()).HasDueWork);
    }

    private static LargeObjectReference Reference(ApplicationOperationReceiptDB row) =>
        new(row.ObjectProvider, row.ObjectName, row.PayloadLength, row.ObjectSha256, row.ObjectEntityTag);

    [TestMethod]
    public async Task BackupRestoreRebindsEncryptedReceiptsAndPreservesNativeReplay()
    {
        await using var rig = await Rig.CreateAsync();
        var operation = Guid.CreateVersion7();
        var original = await rig.InvokeAsync(rig.ContactsBatch(), operation);
        await using var targetDatabase = await PostgresTestDatabase.TryCreateAsync()
            ?? throw new AssertFailedException("PostgreSQL is required.");
        await using var targetSource = NpgsqlDataSource.Create(targetDatabase.ConnectionString);
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
            await DistributedBackupExporter.ExportAsync(rig.Source, rig.Objects, rig.Database.ConnectionString, destination, dump);
            var manifest = await File.ReadAllLinesAsync(Path.Combine(destination, "references.jsonl"));
            Assert.AreEqual(2, manifest.Select(line => JsonSerializer.Deserialize<DistributedBlobReferenceRow>(line))
                .Count(row => row?.Source == "application_operation_receipts.payload_object_name"));
            await DistributedBackupRestorer.RestoreAsync(destination, targetSource, targetDatabase.ConnectionString, objects, restore);
            await DistributedRestoreActivationGuard.RequireReadyAsync(targetSource);
            await using var services = rig.ServicesFor(targetDatabase.ConnectionString, objects);
            using var scope = services.CreateScope();
            var replay = await InvokeGatewayAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(),
                rig.ContactsBatch(), rig.User, operation);
            Assert.AreEqual(JsonSerializer.Serialize(original.Invocations), JsonSerializer.Serialize(replay.Invocations));
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            Assert.AreEqual(2, await database.ApplicationOperationReceipts.CountAsync());
            Assert.AreEqual(1, await database.DavResources.CountAsync(card => card.Uid == "replay-card-uid"));
            await using var originalDatabase = rig.Context();
            Assert.AreNotEqual((await originalDatabase.ApplicationOperationReceipts.OrderBy(receipt => receipt.OperationId).FirstAsync()).ObjectEntityTag,
                (await database.ApplicationOperationReceipts.OrderBy(receipt => receipt.OperationId).FirstAsync()).ObjectEntityTag);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
            temporary.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow(MailOperationKind.None, false)]
    [DataRow(MailOperationKind.None, true)]
    [DataRow((MailOperationKind)999, false)]
    [DataRow((MailOperationKind)999, true)]
    public async Task UnrenderablePrimaryOrAdditionalResultRollsBackBusinessWritesAndReceipt(MailOperationKind invalid, bool additional)
    {
        await using var rig = await Rig.CreateAsync();
        using var scope = rig.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var callbacks = 0;
        var handler = new InvalidResultMethod(database, invalid, additional, () => callbacks++);
        var processor = new JmapRequestProcessor([handler],
            scope.ServiceProvider.GetRequiredService<JmapAccountProfileService>(), database,
            scope.ServiceProvider.GetRequiredService<EnvironmentConfig>(),
            scope.ServiceProvider.GetRequiredService<LargeObjectTransactionEffects>(),
            NullLogger<JmapRequestProcessor>.Instance,
            scope.ServiceProvider.GetRequiredService<ApplicationOperationReceiptStore>());
        var result = await InvokeGatewayAsync(processor, new JmapApplicationBatch([MailFeature.Basic],
            [new(MailOperationKind.MutateContacts, new JsonObject(), "mutation")], new Dictionary<string, string>()),
            rig.User, Guid.CreateVersion7());
        Assert.AreEqual(MailOperationKind.Failure, result.Invocations[0].Operation);
        Assert.AreEqual("serverFail", result.Invocations[0].Arguments["type"]!.GetValue<string>());
        Assert.IsFalse(result.CreatedIds!.ContainsKey("transient"));
        Assert.AreEqual(0, callbacks);
        await using var restored = rig.Context();
        Assert.AreEqual(rig.User.Username, (await restored.Users.SingleAsync()).Username);
        Assert.AreEqual(0, await restored.ApplicationOperationReceipts.CountAsync());
    }

    private static async Task<JmapApplicationBatchResult> InvokeGatewayAsync(
        JmapRequestProcessor processor, JmapApplicationBatch batch, AuthenticatedMailUser user,
        Guid operation, CancellationToken token = default)
    {
        processor.ValidatePlan(new(batch.Features, batch.Invocations.Length));
        var execution = await GatewayJmapBatchExecutor.ExecuteAsync(new ProcessorGatewayJmapClient(processor, user, operation),
            new(ProtocolAuthenticationKinds.Password, user.Username, "test"),
            batch, await processor.GetProfileAsync(user, token), token);
        return execution.Batch!;
    }

    [TestMethod]
    public async Task ReceiptPreservesRawValuesAndGatewayReplaysNormalizedReferences()
    {
        await using var rig = await Rig.CreateAsync();
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
        var first = await processor.ExecuteAsync(command, rig.User, identity);
        var raw = ApplicationValueCodec.Decode(first.Response.Data)!.AsObject();
        Assert.AreEqual("first", raw["\ud800"]!.GetValue<string>());
        Assert.AreEqual("second", raw["\udfff"]!.GetValue<string>());
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(command, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        await using (var transaction = await database.Database.BeginTransactionAsync())
        {
            var saved = await receipts.FindLockedAsync(new(identity, 0, rig.User.Id, "mail.operation", hash), CancellationToken.None);
            Assert.IsNotNull(saved);
            var receipt = JsonSerializer.Deserialize<JmapReplayState>(saved.Result.Span,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 256 })!;
            var persisted = ApplicationValueCodec.Decode(receipt.Response.Data)!.AsObject();
            CollectionAssert.AreEqual(raw.Select(member => member.Key).ToArray(), persisted.Select(member => member.Key).ToArray());
        }
        var replay = await processor.ExecuteAsync(command, rig.User, identity);
        Assert.AreEqual(1, calls);
        var completed = new List<JmapApplicationInvocation> {
            new(MailOperationKind.FindFolders, GatewayJmapJson.SanitizeResponse(ApplicationValueCodec.Decode(replay.Response.Data)!.AsObject()), "source") };
        Assert.IsTrue(GatewayJmapArgumentBindingResolver.TryResolve(new(MailOperationKind.Echo, new JsonObject(), "target",
            [new("copied", "source", MailOperationKind.FindFolders, [new("\ufffd~2")])]),
            completed, out var selected, out _));
        Assert.AreEqual("second", selected["copied"]!.GetValue<string>());
        Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync());
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

    private sealed class InvalidResultMethod(EmailDbContext database, MailOperationKind invalid, bool additional, Action callback) : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.MutateContacts;
        public MailFeature Feature => MailFeature.Basic;
        public async Task<JmapMethodResponse> InvokeAsync(JmapInvocationContext context, JsonObject arguments,
            CancellationToken cancellationToken = default)
        {
            var user = await database.Users.SingleAsync(cancellationToken);
            user.Username = "must-roll-back@example.test";
            await database.SaveChangesAsync(cancellationToken);
            context.CreatedIds["transient"] = "object-id";
            context.AddPostCommitAction(_ => { callback(); return Task.CompletedTask; });
            var unsupported = new JmapMethodResponse(invalid, new JsonObject());
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
            var database = await PostgresTestDatabase.TryCreateAsync();
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
                    await context.Database.EnsureCreatedAsync();
                    await new MailRuntimeSchemaService(context).EnsureAsync();
                    var company = new CompanyDB { Id = Guid.CreateVersion7(), Name = "Replay fixture" };
                    var address = new AddressDB { Id = Guid.CreateVersion7(), Domain = "example.test", Company = company, IsActive = true };
                    var owner = new UserDB { Id = user.Id, Username = user.Username, PasswordHash = "unused", Company = company };
                    context.Inboxes.Add(new InboxDB { Id = inboxId, Name = "replay", Address = address, Owner = owner });
                    await context.SaveChangesAsync();
                }
                await PostgresMessagingSchema.EnsureAsync(source);
                return new Rig(database, source, container, objects, protector, provider, user, inboxId, bus, sink);
            }
            catch
            {
                await provider.DisposeAsync();
                await container.DeleteIfExistsAsync();
                protector.Dispose();
                await source.DisposeAsync();
                await database.DisposeAsync();
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
            var services = new ServiceCollection().AddLogging().AddSingleton(environment).AddSingleton<ILargeObjectStore>(objects)
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
            return await InvokeGatewayAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), batch, User, operation, token);
        }
        public JmapApplicationBatch ContactsBatch() => new([MailFeature.Basic, MailFeature.Contacts],
        [
            new(MailOperationKind.MutateAddressBooks, new JsonObject { ["accountId"] = JmapId.Account(InboxId), ["create"] = new JsonObject
            { ["book"] = new JsonObject { ["name"] = "Replay book" } } }, "book"),
            new(MailOperationKind.MutateContacts, new JsonObject { ["accountId"] = JmapId.Account(InboxId), ["create"] = new JsonObject
            { ["card"] = new JsonObject { ["@type"] = "Card", ["version"] = "1.0", ["uid"] = "replay-card-uid", ["kind"] = "individual",
                ["name"] = new JsonObject { ["@type"] = "Name", ["full"] = "Replay person" }, ["addressBookIds"] = new JsonObject { ["#book"] = true } } } }, "card"),
        ], new Dictionary<string, string>());

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Container.DeleteIfExistsAsync();
            _protector.Dispose();
            await Source.DisposeAsync();
            await Database.DisposeAsync();
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
            await bus.EnqueueAsync(request, cancellationToken);
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
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
    private sealed class CancelReceipt(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<ApplicationOperationReceiptDB>().Any(entry => entry.State == EntityState.Added))
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CancelBeforePublication(IApplicationRequestConsumer consumer, CancellationTokenSource stop) : IApplicationRequestConsumer
    {
        public Task<ApplicationRequestLease> WaitForRequestAsync(string workerId, CancellationToken cancellationToken = default) => consumer.WaitForRequestAsync(workerId, cancellationToken);
        public Task<ApplicationRequestLease?> TryClaimAsync(string workerId, CancellationToken cancellationToken = default) => consumer.TryClaimAsync(workerId, cancellationToken);
        public Task<bool> RenewLeaseAsync(ApplicationRequestLease lease, CancellationToken cancellationToken = default) => consumer.RenewLeaseAsync(lease, cancellationToken);
        public Task FailAsync(ApplicationRequestLease lease, string errorCode, string errorDetail, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Host cancellation must leave the original lease retryable.");
        public Task CompleteAsync(ApplicationRequestLease lease, ApplicationResponse response, CancellationToken cancellationToken = default)
        {
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        }
    }
}
