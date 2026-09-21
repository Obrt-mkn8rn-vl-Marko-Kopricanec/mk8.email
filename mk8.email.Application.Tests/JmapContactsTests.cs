using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class JmapContactsTests
{
    private const string Core = JmapConstants.CoreCapability;
    private const string Contacts = JmapConstants.ContactsCapability;

    [TestMethod]
    public async Task SessionAdvertisesRfc9610ForThePrimaryAccount()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var primaryInbox = await database.Inboxes
            .Include(inbox => inbox.Address)
            .Include(inbox => inbox.Owner)
            .SingleAsync(inbox => inbox.Id == fixture.InboxId);
        var secondaryInboxId = Guid.CreateVersion7();
        database.Inboxes.Add(new InboxDB
        {
            Id = secondaryInboxId,
            Name = "secondary",
            AddressId = primaryInbox.AddressId,
            Address = primaryInbox.Address,
            OwnerId = primaryInbox.OwnerId,
            Owner = primaryInbox.Owner,
        });
        await database.SaveChangesAsync();

        var session = (await scope.ServiceProvider.GetRequiredService<JmapSessionService>()
            .BuildAsync(fixture.User)).Value;

        Assert.IsNotNull(session["capabilities"]?[Contacts]);
        Assert.AreEqual(
            fixture.AccountId,
            session["primaryAccounts"]?[Contacts]?.GetValue<string>());
        var accountCapability = session["accounts"]?[fixture.AccountId]?["accountCapabilities"]?[Contacts];
        Assert.IsNotNull(accountCapability);
        Assert.AreEqual(1, accountCapability["maxAddressBooksPerCard"]!.GetValue<int>());
        Assert.IsTrue(accountCapability["mayCreateAddressBook"]!.GetValue<bool>());
        var secondaryAccountId = JmapId.Account(secondaryInboxId);
        Assert.IsFalse(session["accounts"]![secondaryAccountId]!["accountCapabilities"]!
            .AsObject().ContainsKey(Contacts));

        var unsupported = await InvokeAsync(fixture, "AddressBook/get", new JsonObject
        {
            ["accountId"] = secondaryAccountId,
        });
        Assert.AreEqual("accountNotSupportedByMethod", Arguments(unsupported)["type"]!.GetValue<string>());
        var notFound = await InvokeAsync(fixture, "AddressBook/get", new JsonObject
        {
            ["accountId"] = JmapId.Account(Guid.CreateVersion7()),
        });
        Assert.AreEqual("accountNotFound", Arguments(notFound)["type"]!.GetValue<string>());

        var collections = await database.DavCollections.AsNoTracking()
            .Where(collection => collection.UserId == fixture.User.Id
                && collection.CollectionType == DavCollectionDB.AddressBookType)
            .ToListAsync();
        Assert.AreEqual(1, collections.Count);
        Assert.IsTrue(collections[0].IsDefault);
        Assert.IsTrue(collections[0].IsSubscribed);
    }

    [TestMethod]
    public async Task AddressBookLifecycleTracksChangesAndProtectsTheDefault()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var initial = Arguments(await InvokeAsync(fixture, "AddressBook/get", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
        }));
        var defaultBookId = initial["list"]![0]!["id"]!.GetValue<string>();
        var initialState = initial["state"]!.GetValue<string>();
        Assert.IsTrue(initial["list"]![0]!["isDefault"]!.GetValue<bool>());
        Assert.IsFalse(initial["list"]![0]!["myRights"]!["mayDelete"]!.GetValue<bool>());

        var create = Arguments(await InvokeAsync(fixture, "AddressBook/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject
            {
                ["work"] = new JsonObject
                {
                    ["name"] = "Work",
                    ["description"] = "Company directory",
                    ["sortOrder"] = 20,
                    ["isSubscribed"] = false,
                },
            },
            ["onSuccessSetIsDefault"] = "#work",
        }));
        var workBookId = create["created"]!["work"]!["id"]!.GetValue<string>();
        Assert.IsTrue(create["created"]!["work"]!["isDefault"]!.GetValue<bool>());
        Assert.IsFalse(create["created"]!["work"]!["myRights"]!["mayDelete"]!.GetValue<bool>());
        Assert.IsFalse(create["updated"]![defaultBookId]!["isDefault"]!.GetValue<bool>());
        Assert.IsFalse(create["updated"]![defaultBookId]!["myRights"]!["mayDelete"]!.GetValue<bool>());
        var createdBook = Arguments(await InvokeAsync(fixture, "AddressBook/get", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["ids"] = new JsonArray(workBookId),
        }))["list"]![0]!;
        Assert.IsFalse(createdBook["isSubscribed"]!.GetValue<bool>());

        var changes = Arguments(await InvokeAsync(fixture, "AddressBook/changes", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["sinceState"] = initialState,
        }));
        CollectionAssert.Contains(StringValues(changes["created"]!), workBookId);
        CollectionAssert.Contains(StringValues(changes["updated"]!), defaultBookId);

        var protectedDestroy = Arguments(await InvokeAsync(fixture, "AddressBook/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["destroy"] = new JsonArray(workBookId),
        }));
        Assert.AreEqual(
            "forbidden",
            protectedDestroy["notDestroyed"]![workBookId]!["type"]!.GetValue<string>());

        var invalidCreate = Arguments(await InvokeAsync(fixture, "AddressBook/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject
            {
                ["bad"] = new JsonObject { ["name"] = "Bad", ["unknown"] = true },
            },
        }));
        Assert.AreEqual(
            "invalidProperties",
            invalidCreate["notCreated"]!["bad"]!["type"]!.GetValue<string>());
        CollectionAssert.Contains(
            StringValues(invalidCreate["notCreated"]!["bad"]!["properties"]!),
            "unknown");

        var forbiddenShare = Arguments(await InvokeAsync(fixture, "AddressBook/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject
            {
                ["shared"] = new JsonObject
                {
                    ["name"] = "Shared",
                    ["shareWith"] = new JsonObject { ["principal"] = new JsonObject() },
                },
            },
        }));
        Assert.AreEqual(
            "forbidden",
            forbiddenShare["notCreated"]!["shared"]!["type"]!.GetValue<string>());

        var emptyDefault = await InvokeAsync(fixture, "AddressBook/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["onSuccessSetIsDefault"] = string.Empty,
        });
        Assert.AreEqual("error", emptyDefault["methodResponses"]![0]![0]!.GetValue<string>());
        Assert.AreEqual("invalidArguments", Arguments(emptyDefault)["type"]!.GetValue<string>());

        var update = Arguments(await InvokeAsync(fixture, "AddressBook/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["update"] = new JsonObject
            {
                [workBookId] = new JsonObject { ["name"] = "Colleagues", ["isSubscribed"] = true },
            },
            ["onSuccessSetIsDefault"] = defaultBookId,
        }));
        Assert.IsNotNull(update["updated"]?[workBookId]);
        Assert.IsNotNull(update["updated"]?[defaultBookId]);

        var destroy = Arguments(await InvokeAsync(fixture, "AddressBook/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["destroy"] = new JsonArray(workBookId),
        }));
        CollectionAssert.AreEqual(new[] { workBookId }, StringValues(destroy["destroyed"]!));
    }

    [TestMethod]
    public async Task ContactCardsSupportCrudQueryChangesAndAddressBookMoves()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var defaultBookId = await GetDefaultAddressBookIdAsync(fixture);
        var initialAddressBookState = Arguments(await InvokeAsync(
            fixture,
            "AddressBook/get",
            new JsonObject { ["accountId"] = fixture.AccountId }))["state"]!.GetValue<string>();
        var initialQuery = Arguments(await InvokeAsync(fixture, "ContactCard/query", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["calculateTotal"] = true,
        }));
        var initialState = initialQuery["queryState"]!.GetValue<string>();
        Assert.AreEqual(0, initialQuery["total"]!.GetValue<int>());

        var create = Arguments(await InvokeAsync(fixture, "ContactCard/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject
            {
                ["alice"] = Card(
                    defaultBookId,
                    "alice-uid",
                    "Alice Adams",
                    "Alice",
                    "Adams",
                    "alice@example.net",
                    new JsonObject { ["mk8.email:client"] = new JsonObject { ["theme"] = "blue" } }),
                ["bob"] = Card(
                    defaultBookId,
                    "bob-uid",
                    "Bob Brown",
                    "Bob",
                    "Brown",
                    "bob@example.net"),
            },
        }));
        var aliceId = create["created"]!["alice"]!["id"]!.GetValue<string>();
        var bobId = create["created"]!["bob"]!["id"]!.GetValue<string>();
        var createdState = create["newState"]!.GetValue<string>();
        Assert.IsTrue(aliceId.StartsWith('C'));
        Assert.IsTrue(bobId.StartsWith('C'));
        var addressBookStateAfterCardCreation = Arguments(await InvokeAsync(
            fixture,
            "AddressBook/get",
            new JsonObject { ["accountId"] = fixture.AccountId }))["state"]!.GetValue<string>();
        Assert.AreEqual(initialAddressBookState, addressBookStateAfterCardCreation);

        var get = Arguments(await InvokeAsync(fixture, "ContactCard/get", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["ids"] = new JsonArray(aliceId),
        }));
        var alice = get["list"]![0]!;
        Assert.AreEqual("Alice Adams", alice["name"]?["full"]?.GetValue<string>());
        Assert.AreEqual("blue", alice["mk8.email:client"]?["theme"]?.GetValue<string>());
        Assert.IsTrue(alice["addressBookIds"]?[defaultBookId]!.GetValue<bool>() ?? false);

        var invalidProperties = await InvokeAsync(fixture, "ContactCard/get", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["ids"] = new JsonArray(aliceId),
            ["properties"] = new JsonArray("x-invalid-name"),
        });
        Assert.AreEqual("error", invalidProperties["methodResponses"]![0]![0]!.GetValue<string>());
        Assert.AreEqual(
            "invalidArguments",
            Arguments(invalidProperties)["type"]!.GetValue<string>());

        using (var scope = fixture.Services.CreateScope())
        {
            var resource = await scope.ServiceProvider.GetRequiredService<EmailDbContext>()
                .DavResources.AsNoTracking().SingleAsync(item => item.Uid == "alice-uid");
            var vcard = Encoding.UTF8.GetString(resource.Content);
            StringAssert.Contains(vcard, "FN:Alice Adams");
            StringAssert.Contains(vcard, ":alice@example.net");
            StringAssert.Contains(vcard, "X-MK8-JSCONTACT:");
        }

        var query = Arguments(await InvokeAsync(fixture, "ContactCard/query", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["filter"] = new JsonObject { ["email"] = "alice@example.net" },
            ["sort"] = new JsonArray(new JsonObject
            {
                ["property"] = "name/surname",
                ["isAscending"] = true,
            }),
            ["calculateTotal"] = true,
        }));
        CollectionAssert.AreEqual(new[] { aliceId }, StringValues(query["ids"]!));
        Assert.AreEqual(1, query["total"]!.GetValue<int>());

        var queryChanges = Arguments(await InvokeAsync(fixture, "ContactCard/queryChanges", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["sinceQueryState"] = initialState,
            ["calculateTotal"] = true,
        }));
        CollectionAssert.AreEquivalent(
            new[] { aliceId, bobId },
            queryChanges["added"]!.AsArray()
                .Select(item => item!["id"]!.GetValue<string>()).ToArray());
        Assert.AreEqual(2, queryChanges["total"]!.GetValue<int>());

        var changes = Arguments(await InvokeAsync(fixture, "ContactCard/changes", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["sinceState"] = initialState,
        }));
        CollectionAssert.AreEquivalent(new[] { aliceId, bobId }, StringValues(changes["created"]!));

        var duplicate = Arguments(await InvokeAsync(fixture, "ContactCard/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject
            {
                ["duplicate"] = Card(
                    defaultBookId,
                    "alice-uid",
                    "Duplicate Alice",
                    "Alice",
                    "Duplicate",
                    "duplicate@example.net"),
            },
        }));
        Assert.AreEqual(
            "invalidProperties",
            duplicate["notCreated"]!["duplicate"]!["type"]!.GetValue<string>());
        CollectionAssert.Contains(
            StringValues(duplicate["notCreated"]!["duplicate"]!["properties"]!),
            "uid");

        var secondary = Arguments(await InvokeAsync(fixture, "AddressBook/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject { ["team"] = new JsonObject { ["name"] = "Team" } },
        }));
        var teamBookId = secondary["created"]!["team"]!["id"]!.GetValue<string>();
        var move = Arguments(await InvokeAsync(fixture, "ContactCard/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["ifInState"] = createdState,
            ["update"] = new JsonObject
            {
                [aliceId] = new JsonObject
                {
                    ["addressBookIds"] = new JsonObject { [teamBookId] = true },
                    ["name/full"] = "Alice A. Adams",
                },
            },
        }));
        Assert.IsTrue(move["updated"]!.AsObject().ContainsKey(aliceId));
        Assert.IsNull(move["updated"]![aliceId]);
        var movedState = move["newState"]!.GetValue<string>();

        var movedQuery = Arguments(await InvokeAsync(fixture, "ContactCard/query", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["filter"] = new JsonObject { ["inAddressBook"] = teamBookId },
        }));
        CollectionAssert.AreEqual(new[] { aliceId }, StringValues(movedQuery["ids"]!));

        var contentGuard = Arguments(await InvokeAsync(fixture, "AddressBook/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["destroy"] = new JsonArray(teamBookId),
        }));
        Assert.AreEqual(
            "addressBookHasContents",
            contentGuard["notDestroyed"]![teamBookId]!["type"]!.GetValue<string>());

        var cascade = Arguments(await InvokeAsync(fixture, "AddressBook/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["destroy"] = new JsonArray(teamBookId),
            ["onDestroyRemoveContents"] = true,
        }));
        CollectionAssert.AreEqual(new[] { teamBookId }, StringValues(cascade["destroyed"]!));
        var destroyedChanges = Arguments(await InvokeAsync(fixture, "ContactCard/changes", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["sinceState"] = movedState,
        }));
        CollectionAssert.Contains(StringValues(destroyedChanges["destroyed"]!), aliceId);
    }

    [TestMethod]
    public async Task ContactCardQueryChangesDoesNotReinsertUpdatesForImmutableIdOrder()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var defaultBookId = await GetDefaultAddressBookIdAsync(fixture);
        var create = Arguments(await InvokeAsync(fixture, "ContactCard/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject
            {
                ["alice"] = Card(
                    defaultBookId,
                    "immutable-query-alice",
                    "Alice Adams",
                    "Alice",
                    "Adams",
                    "alice@example.net"),
                ["bob"] = Card(
                    defaultBookId,
                    "immutable-query-bob",
                    "Bob Baker",
                    "Bob",
                    "Baker",
                    "bob@example.net"),
            },
        }));
        var aliceId = create["created"]!["alice"]!["id"]!.GetValue<string>();

        var immutableQuery = Arguments(await InvokeAsync(fixture, "ContactCard/query", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
        }));
        var immutableState = immutableQuery["queryState"]!.GetValue<string>();

        var contentOnlyUpdate = Arguments(await InvokeAsync(fixture, "ContactCard/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["update"] = new JsonObject
            {
                [aliceId] = new JsonObject { ["name/full"] = "Alice A. Adams" },
            },
        }));
        Assert.IsTrue(contentOnlyUpdate["updated"]!.AsObject().ContainsKey(aliceId));

        var immutableChanges = Arguments(await InvokeAsync(
            fixture,
            "ContactCard/queryChanges",
            new JsonObject
            {
                ["accountId"] = fixture.AccountId,
                ["sinceQueryState"] = immutableState,
            }));
        Assert.AreEqual(0, immutableChanges["removed"]!.AsArray().Count);
        Assert.AreEqual(0, immutableChanges["added"]!.AsArray().Count);

        var mutableSort = new JsonArray(new JsonObject
        {
            ["property"] = "name/surname",
            ["isAscending"] = true,
        });
        var mutableQuery = Arguments(await InvokeAsync(fixture, "ContactCard/query", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["sort"] = mutableSort.DeepClone(),
        }));
        var mutableState = mutableQuery["queryState"]!.GetValue<string>();

        var orderUpdate = Arguments(await InvokeAsync(fixture, "ContactCard/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["update"] = new JsonObject
            {
                [aliceId] = new JsonObject
                {
                    ["name"] = new JsonObject
                    {
                        ["full"] = "Alice Zulu",
                        ["components"] = new JsonArray(
                            new JsonObject { ["kind"] = "given", ["value"] = "Alice" },
                            new JsonObject { ["kind"] = "surname", ["value"] = "Zulu" }),
                    },
                },
            },
        }));
        Assert.IsTrue(orderUpdate["updated"]!.AsObject().ContainsKey(aliceId));

        var mutableChanges = Arguments(await InvokeAsync(
            fixture,
            "ContactCard/queryChanges",
            new JsonObject
            {
                ["accountId"] = fixture.AccountId,
                ["sort"] = mutableSort.DeepClone(),
                ["sinceQueryState"] = mutableState,
            }));
        CollectionAssert.AreEqual(new[] { aliceId }, StringValues(mutableChanges["removed"]!));
        var added = mutableChanges["added"]!.AsArray();
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual(aliceId, added[0]!["id"]!.GetValue<string>());
        Assert.AreEqual(1, added[0]!["index"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ContactMediaAcceptsTypedUploadsAndRejectsMismatches()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var defaultBookId = await GetDefaultAddressBookIdAsync(fixture);
        var imageBlobId = await fixture.StoreBlobAsync(
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a],
            "image/png");
        var documentBlobId = await fixture.StoreBlobAsync(
            Encoding.ASCII.GetBytes("not an image"),
            "application/pdf");

        var good = Card(
            defaultBookId,
            "photo-contact",
            "Photo Contact",
            "Photo",
            "Contact",
            "photo@example.net");
        good["media"] = new JsonObject
        {
            ["photo"] = new JsonObject { ["kind"] = "photo", ["blobId"] = imageBlobId },
        };
        var bad = Card(
            defaultBookId,
            "bad-photo-contact",
            "Bad Photo",
            "Bad",
            "Photo",
            "bad-photo@example.net");
        bad["media"] = new JsonObject
        {
            ["photo"] = new JsonObject { ["kind"] = "photo", ["blobId"] = documentBlobId },
        };
        var expectedCache = (JsonObject)good.DeepClone();

        var set = Arguments(await InvokeAsync(fixture, "ContactCard/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject { ["good"] = good, ["bad"] = bad },
        }));
        var created = set["created"]!["good"]!.AsObject();
        var cardId = created["id"]!.GetValue<string>();
        var createdPhoto = created["media"]!["photo"]!;
        Assert.IsNull(createdPhoto["blobId"]);
        Assert.AreEqual("image/png", createdPhoto["mediaType"]!.GetValue<string>());
        StringAssert.StartsWith(
            createdPhoto["uri"]!.GetValue<string>(),
            "data:image/png;base64,");
        ApplyServerProperties(expectedCache, created);
        Assert.AreEqual(
            "invalidProperties",
            set["notCreated"]!["bad"]!["type"]!.GetValue<string>());
        CollectionAssert.Contains(
            StringValues(set["notCreated"]!["bad"]!["properties"]!),
            "media/photo/blobId");

        var get = Arguments(await InvokeAsync(fixture, "ContactCard/get", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["ids"] = new JsonArray(cardId),
        }));
        var photo = get["list"]![0]!["media"]!["photo"]!;
        Assert.AreEqual("image/png", photo["mediaType"]!.GetValue<string>());
        StringAssert.StartsWith(photo["uri"]!.GetValue<string>(), "data:image/png;base64,");
        Assert.IsNull(photo["blobId"]);
        Assert.IsTrue(JsonNode.DeepEquals(expectedCache, get["list"]![0]));

        var replacementBlobId = await fixture.StoreBlobAsync(
            [0x47, 0x49, 0x46, 0x38, 0x39, 0x61],
            "image/gif");
        var clientPatch = new JsonObject
        {
            ["media/photo"] = new JsonObject
            {
                ["kind"] = "photo",
                ["blobId"] = replacementBlobId,
            },
        };
        Assert.IsTrue(JmapMethodHelpers.TryApplyPatch(expectedCache, clientPatch, out var patchedCache));
        var update = Arguments(await InvokeAsync(fixture, "ContactCard/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["update"] = new JsonObject { [cardId] = clientPatch },
        }));
        var updated = update["updated"]![cardId]!.AsObject();
        var updatedPhoto = updated["media"]!["photo"]!;
        Assert.IsNull(updatedPhoto["blobId"]);
        Assert.AreEqual("image/gif", updatedPhoto["mediaType"]!.GetValue<string>());
        StringAssert.StartsWith(updatedPhoto["uri"]!.GetValue<string>(), "data:image/gif;base64,");
        ApplyServerProperties(patchedCache, updated);

        var updatedGet = Arguments(await InvokeAsync(fixture, "ContactCard/get", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["ids"] = new JsonArray(cardId),
        }));
        Assert.IsTrue(JsonNode.DeepEquals(patchedCache, updatedGet["list"]![0]));

        using var scope = fixture.Services.CreateScope();
        var content = await scope.ServiceProvider.GetRequiredService<EmailDbContext>()
            .DavResources.AsNoTracking()
            .Where(resource => resource.Uid == "photo-contact")
            .Select(resource => resource.Content)
            .SingleAsync();
        StringAssert.Contains(Encoding.UTF8.GetString(content), "data:image/gif;base64,");
    }

    [TestMethod]
    public async Task ContactMediaAuthorizesEveryLocalizedBlobBeforePersistingIt()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var defaultBookId = await GetDefaultAddressBookIdAsync(fixture);
        var localizedBlobId = await fixture.StoreBlobAsync(
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a],
            "image/png");
        var foreignBlobId = await fixture.StoreBlobAsync([0x47, 0x49, 0x46], "image/gif");
        var expiredBlobId = await fixture.StoreBlobAsync([0x52, 0x49, 0x46, 0x46], "audio/wav");
        var wrongTypeBlobId = await fixture.StoreBlobAsync(
            Encoding.ASCII.GetBytes("not an image"),
            "application/pdf");
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var foreign = await database.JmapBlobs.SingleAsync(blob => blob.BlobId == foreignBlobId);
            foreign.AccountId = Guid.CreateVersion7();
            var expired = await database.JmapBlobs.SingleAsync(blob => blob.BlobId == expiredBlobId);
            expired.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await database.SaveChangesAsync();
        }

        var localized = Card(
            defaultBookId,
            "localized-photo",
            "Localized Photo",
            "Localized",
            "Photo",
            "localized@example.net");
        localized["localizations"] = new JsonObject
        {
            ["fr"] = new JsonObject
            {
                ["media"] = Media(localizedBlobId, "photo"),
            },
        };
        var missing = WithMedia("missing-photo", JmapId.UploadedBlob(Guid.CreateVersion7()), "photo");
        var foreignCard = WithMedia("foreign-photo", foreignBlobId, "photo");
        var expiredCard = WithMedia("expired-sound", expiredBlobId, "sound");
        var wrongType = WithMedia("wrong-type-photo", wrongTypeBlobId, "photo");
        var localizedMissing = Card(
            defaultBookId,
            "localized-missing-photo",
            "Localized Missing",
            "Localized",
            "Missing",
            "localized-missing@example.net");
        localizedMissing["localizations"] = new JsonObject
        {
            ["de"] = new JsonObject
            {
                ["media"] = Media(JmapId.UploadedBlob(Guid.CreateVersion7()), "photo"),
            },
        };

        var set = Arguments(await InvokeAsync(fixture, "ContactCard/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject
            {
                ["localized"] = localized,
                ["missing"] = missing,
                ["foreign"] = foreignCard,
                ["expired"] = expiredCard,
                ["wrongType"] = wrongType,
                ["localizedMissing"] = localizedMissing,
            },
        }));

        var cardId = set["created"]!["localized"]!["id"]!.GetValue<string>();
        var createdLocalization = set["created"]!["localized"]!["localizations"]!["fr"]!;
        Assert.IsNull(createdLocalization["media"]!["photo"]!["blobId"]);
        StringAssert.StartsWith(
            createdLocalization["media"]!["photo"]!["uri"]!.GetValue<string>(),
            "data:image/png;base64,");
        foreach (var expected in new Dictionary<string, string>
        {
            ["missing"] = "media/photo/blobId",
            ["foreign"] = "media/photo/blobId",
            ["expired"] = "media/sound/blobId",
            ["wrongType"] = "media/photo/blobId",
        })
        {
            Assert.AreEqual(
                "invalidProperties",
                set["notCreated"]![expected.Key]!["type"]!.GetValue<string>());
            CollectionAssert.Contains(
                StringValues(set["notCreated"]![expected.Key]!["properties"]!),
                expected.Value);
        }
        CollectionAssert.Contains(
            StringValues(set["notCreated"]!["localizedMissing"]!["properties"]!),
            "localizations/de/media/photo/blobId");

        var get = Arguments(await InvokeAsync(fixture, "ContactCard/get", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["ids"] = new JsonArray(cardId),
        }));
        var normalized = get["list"]![0]!["localizations"]!["fr"]!["media"]!["photo"]!;
        Assert.IsNull(normalized["blobId"]);
        Assert.AreEqual("image/png", normalized["mediaType"]!.GetValue<string>());
        StringAssert.StartsWith(normalized["uri"]!.GetValue<string>(), "data:image/png;base64,");

        JsonObject WithMedia(string uid, string blobId, string kind)
        {
            var card = Card(
                defaultBookId,
                uid,
                uid,
                "Media",
                "Test",
                $"{uid}@example.net");
            card["media"] = Media(blobId, kind);
            return card;
        }

        static JsonObject Media(string blobId, string kind) => new()
        {
            [kind] = new JsonObject { ["kind"] = kind, ["blobId"] = blobId },
        };
    }

    [TestMethod]
    public async Task ContactValidationRejectsMalformedRegisteredDataAndFoldsUtf8Vcards()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var defaultBookId = await GetDefaultAddressBookIdAsync(fixture);
        var badProperty = Card(
            defaultBookId,
            "bad-property",
            "Bad Property",
            "Bad",
            "Property",
            "bad-property@example.net");
        badProperty["x-invalid-name"] = true;
        var badEmail = Card(
            defaultBookId,
            "bad-email",
            "Bad Email",
            "Bad",
            "Email",
            "Display Name <bad-email@example.net>");
        var reserved = Card(
            defaultBookId,
            "reserved-property",
            "Reserved Property",
            "Reserved",
            "Property",
            "reserved@example.net");
        reserved["extra"] = new JsonObject();

        var invalid = Arguments(await InvokeAsync(fixture, "ContactCard/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject
            {
                ["badProperty"] = badProperty,
                ["badEmail"] = badEmail,
                ["reserved"] = reserved,
            },
        }));
        CollectionAssert.Contains(
            StringValues(invalid["notCreated"]!["badProperty"]!["properties"]!),
            "x-invalid-name");
        CollectionAssert.Contains(
            StringValues(invalid["notCreated"]!["badEmail"]!["properties"]!),
            "emails");
        CollectionAssert.Contains(
            StringValues(invalid["notCreated"]!["reserved"]!["properties"]!),
            "extra");

        var longName = string.Concat(Enumerable.Repeat("Ž😀", 40));
        var valid = Card(
            defaultBookId,
            "utf8-folding",
            longName,
            "Željko",
            "Unicode",
            "unicode@example.net",
            new JsonObject { ["mk8.email:metadata"] = new JsonObject { ["value"] = longName } });
        var create = Arguments(await InvokeAsync(fixture, "ContactCard/set", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject { ["unicode"] = valid },
        }));
        var cardId = create["created"]!["unicode"]!["id"]!.GetValue<string>();

        using (var scope = fixture.Services.CreateScope())
        {
            var content = await scope.ServiceProvider.GetRequiredService<EmailDbContext>()
                .DavResources.AsNoTracking()
                .Where(resource => resource.Uid == "utf8-folding")
                .Select(resource => resource.Content)
                .SingleAsync();
            var lines = Encoding.UTF8.GetString(content)
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.IsTrue(lines.All(line => Encoding.UTF8.GetByteCount(line) <= 75));
            Assert.IsTrue(lines.Any(line => line.StartsWith(' ')));
        }

        var get = Arguments(await InvokeAsync(fixture, "ContactCard/get", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["ids"] = new JsonArray(cardId),
        }));
        Assert.AreEqual(longName, get["list"]![0]!["name"]!["full"]!.GetValue<string>());
        Assert.AreEqual(
            longName,
            get["list"]![0]!["mk8.email:metadata"]!["value"]!.GetValue<string>());
    }

    private static JsonObject Card(
        string addressBookId,
        string uid,
        string fullName,
        string givenName,
        string surname,
        string email,
        JsonObject? additions = null)
    {
        var card = new JsonObject
        {
            ["@type"] = "Card",
            ["version"] = "1.0",
            ["uid"] = uid,
            ["kind"] = "individual",
            ["name"] = new JsonObject
            {
                ["full"] = fullName,
                ["components"] = new JsonArray(
                    new JsonObject { ["kind"] = "given", ["value"] = givenName },
                    new JsonObject { ["kind"] = "surname", ["value"] = surname }),
            },
            ["emails"] = new JsonObject
            {
                ["work"] = new JsonObject
                {
                    ["address"] = email,
                    ["contexts"] = new JsonObject { ["work"] = true },
                },
            },
            ["addressBookIds"] = new JsonObject { [addressBookId] = true },
        };
        if (additions is not null)
        {
            foreach (var addition in additions)
                card[addition.Key] = addition.Value?.DeepClone();
        }
        return card;
    }

    private static async Task<string> GetDefaultAddressBookIdAsync(JmapFixture fixture)
    {
        var response = Arguments(await InvokeAsync(fixture, "AddressBook/get", new JsonObject
        {
            ["accountId"] = fixture.AccountId,
        }));
        return response["list"]!.AsArray()
            .Single(book => book!["isDefault"]!.GetValue<bool>())!["id"]!.GetValue<string>();
    }

    private static Task<JsonObject> InvokeAsync(
        JmapFixture fixture,
        string method,
        JsonObject arguments) => fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core, Contacts),
            ["methodCalls"] = new JsonArray(new JsonArray(method, arguments, "c1")),
        });

    private static JsonObject Arguments(JsonObject response) =>
        response["methodResponses"]![0]![1]!.AsObject();

    private static void ApplyServerProperties(JsonObject target, JsonObject serverProperties)
    {
        foreach (var property in serverProperties)
        {
            if (property.Value is null)
                target.Remove(property.Key);
            else
                target[property.Key] = property.Value.DeepClone();
        }
    }

    private static string[] StringValues(JsonNode node) => node.AsArray()
        .Select(item => item!.GetValue<string>()).ToArray();
}
