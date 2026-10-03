using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class SieveScriptServiceTests
{
    [TestMethod]
    public async Task ScriptLifecycleValidatesContentAndMaintainsSingleActiveScript()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var userId = Guid.CreateVersion7();
        await (database.Users.AddAsync(new UserDB
        {
            Id = userId,
            Username = "admin@mk8n.com",
            PasswordHash = "unused",
            Role = "User",
        })).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        var service = CreateService(database);

        var invalid = await service.PutAsync(userId, "invalid", "fileinto \"Archive\";").ConfigureAwait(false);
        Assert.IsFalse(invalid.Succeeded);
        Assert.AreEqual(0, await database.SieveScripts.CountAsync().ConfigureAwait(false));

        Assert.IsTrue((await service.PutAsync(userId, "first", "keep;").ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await service.PutAsync(userId, "second", "discard;").ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await service.SetActiveAsync(userId, "first").ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await service.SetActiveAsync(userId, "second").ConfigureAwait(false)).Succeeded);

        var scripts = await service.ListAsync(userId).ConfigureAwait(false);
        Assert.AreEqual(2, scripts.Count);
        Assert.AreEqual("second", scripts.Single(item => item.IsActive).Name, StringComparer.Ordinal);
        Assert.IsFalse((await service.SetActiveAsync(userId, "missing").ConfigureAwait(false)).Succeeded);
        Assert.AreEqual(
            "second",
            (await service.ListAsync(userId).ConfigureAwait(false)).Single(item => item.IsActive).Name, StringComparer.Ordinal);
        Assert.IsFalse((await service.DeleteAsync(userId, "second").ConfigureAwait(false)).Succeeded);

        Assert.IsTrue((await service.SetActiveAsync(userId, null).ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await service.RenameAsync(userId, "first", "renamed").ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await service.DeleteAsync(userId, "second").ConfigureAwait(false)).Succeeded);
        var stored = await service.GetAsync(userId, "renamed").ConfigureAwait(false);
        Assert.IsNotNull(stored);
        Assert.AreEqual("keep;", stored.Content, StringComparer.Ordinal);
        Assert.IsFalse(stored.IsActive);
    }

    [TestMethod]
    public async Task ScriptNamesAreNormalizedAndBounded()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var userId = Guid.CreateVersion7();
        await (database.Users.AddAsync(new UserDB
        {
            Id = userId,
            Username = "admin@mk8n.com",
            PasswordHash = "unused",
            Role = "User",
        })).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        var service = CreateService(database);

        Assert.IsFalse((await service.PutAsync(userId, string.Empty, "keep;").ConfigureAwait(false)).Succeeded);
        Assert.IsFalse((await service.PutAsync(userId, new string('x', 129), "keep;").ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await service.PutAsync(userId, string.Concat(Enumerable.Repeat("😀", 128)), "keep;").ConfigureAwait(false)).Succeeded);
        Assert.IsFalse((await service.PutAsync(userId, string.Concat(Enumerable.Repeat("😀", 129)), "keep;").ConfigureAwait(false)).Succeeded);
        Assert.IsFalse((await service.PutAsync(userId, "line\u2028separator", "keep;").ConfigureAwait(false)).Succeeded);
        Assert.IsFalse((await service.PutAsync(userId, "invalid-unicode", "keep;\ud800").ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await service.PutAsync(userId, "Cafe\u0301", "keep;").ConfigureAwait(false)).Succeeded);
        Assert.IsNotNull(await service.GetAsync(userId, "Café").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ScriptCountQuotaAllowsReplacementButRejectsAdditionalScripts()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var userId = Guid.CreateVersion7();
        await (database.Users.AddAsync(new UserDB
        {
            Id = userId,
            Username = "admin@mk8n.com",
            PasswordHash = "unused",
            Role = "User",
        })).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        var service = CreateService(database);

        Assert.IsTrue((await service.PutAsync(userId, "first", "keep;", 1).ConfigureAwait(false)).Succeeded);
        var original = await service.GetAsync(userId, "first").ConfigureAwait(false);
        Assert.IsNotNull(original);
        Assert.IsTrue((await service.SetActiveAsync(userId, "first").ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await service.PutAsync(userId, "first", "discard;", 1).ConfigureAwait(false)).Succeeded);
        var replacement = await service.GetAsync(userId, "first").ConfigureAwait(false);
        Assert.IsNotNull(replacement);
        Assert.AreEqual(original.CreatedAt, replacement.CreatedAt);
        Assert.AreEqual("discard;", replacement.Content, StringComparer.Ordinal);
        Assert.IsTrue(replacement.IsActive);
        var rejected = await service.PutAsync(userId, "second", "keep;", 1).ConfigureAwait(false);
        Assert.IsFalse(rejected.Succeeded);
        Assert.AreEqual("QUOTA/MAXSCRIPTS", rejected.ResponseCode, StringComparer.Ordinal);
        Assert.IsNull(await service.GetAsync(userId, "second").ConfigureAwait(false));
        Assert.HasCount(1, await service.ListAsync(userId).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PutValidatesContentBeforeCheckingUnknownUser()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var service = CreateService(database);
        var userId = Guid.CreateVersion7();

        var empty = await service.PutAsync(userId, "first", string.Empty).ConfigureAwait(false);
        Assert.IsFalse(empty.Succeeded);
        Assert.AreEqual("QUOTA/MAXSIZE", empty.ResponseCode, StringComparer.Ordinal);

        var invalidUnicode = await service.PutAsync(userId, "first", "keep;\ud800").ConfigureAwait(false);
        Assert.IsFalse(invalidUnicode.Succeeded);
        Assert.AreEqual("The script contains invalid Unicode.", invalidUnicode.Error, StringComparer.Ordinal);

        var unknownUser = await service.PutAsync(userId, "first", "keep;").ConfigureAwait(false);
        Assert.IsFalse(unknownUser.Succeeded);
        Assert.AreEqual("The user does not exist.", unknownUser.Error, StringComparer.Ordinal);
        Assert.AreEqual(0, await database.SieveScripts.CountAsync().ConfigureAwait(false));
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"sieve-scripts-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new EmailDbContext(options);
    }

    private static SieveScriptService CreateService(EmailDbContext database)
    {
        var store = new InMemoryLargeObjectStore();
        var effects = new LargeObjectTransactionEffects(
            store,
            NullLogger<LargeObjectTransactionEffects>.Instance);
        return new SieveScriptService(
            database,
            new SieveScriptContentService(store, effects),
            effects,
            NullLogger<SieveScriptService>.Instance);
    }
}
