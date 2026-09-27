using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class SieveScriptServiceTests
{
    [TestMethod]
    public async Task ScriptLifecycleValidatesContentAndMaintainsSingleActiveScript()
    {
        await using var database = CreateDatabase();
        await database.Database.EnsureCreatedAsync();
        var userId = Guid.CreateVersion7();
        database.Users.Add(new UserDB
        {
            Id = userId,
            Username = "admin@mk8n.com",
            PasswordHash = "unused",
            Role = "User",
        });
        await database.SaveChangesAsync();
        var service = CreateService(database);

        var invalid = await service.PutAsync(userId, "invalid", "fileinto \"Archive\";");
        Assert.IsFalse(invalid.Succeeded);
        Assert.AreEqual(0, await database.SieveScripts.CountAsync());

        Assert.IsTrue((await service.PutAsync(userId, "first", "keep;")).Succeeded);
        Assert.IsTrue((await service.PutAsync(userId, "second", "discard;")).Succeeded);
        Assert.IsTrue((await service.SetActiveAsync(userId, "first")).Succeeded);
        Assert.IsTrue((await service.SetActiveAsync(userId, "second")).Succeeded);

        var scripts = await service.ListAsync(userId);
        Assert.AreEqual(2, scripts.Count);
        Assert.AreEqual("second", scripts.Single(item => item.IsActive).Name);
        Assert.IsFalse((await service.SetActiveAsync(userId, "missing")).Succeeded);
        Assert.AreEqual(
            "second",
            (await service.ListAsync(userId)).Single(item => item.IsActive).Name);
        Assert.IsFalse((await service.DeleteAsync(userId, "second")).Succeeded);

        Assert.IsTrue((await service.SetActiveAsync(userId, null)).Succeeded);
        Assert.IsTrue((await service.RenameAsync(userId, "first", "renamed")).Succeeded);
        Assert.IsTrue((await service.DeleteAsync(userId, "second")).Succeeded);
        var stored = await service.GetAsync(userId, "renamed");
        Assert.IsNotNull(stored);
        Assert.AreEqual("keep;", stored.Content);
        Assert.IsFalse(stored.IsActive);
    }

    [TestMethod]
    public async Task ScriptNamesAreNormalizedAndBounded()
    {
        await using var database = CreateDatabase();
        await database.Database.EnsureCreatedAsync();
        var userId = Guid.CreateVersion7();
        database.Users.Add(new UserDB
        {
            Id = userId,
            Username = "admin@mk8n.com",
            PasswordHash = "unused",
            Role = "User",
        });
        await database.SaveChangesAsync();
        var service = CreateService(database);

        Assert.IsFalse((await service.PutAsync(userId, string.Empty, "keep;")).Succeeded);
        Assert.IsFalse((await service.PutAsync(userId, new string('x', 129), "keep;")).Succeeded);
        Assert.IsTrue((await service.PutAsync(userId, string.Concat(Enumerable.Repeat("😀", 128)), "keep;")).Succeeded);
        Assert.IsFalse((await service.PutAsync(userId, string.Concat(Enumerable.Repeat("😀", 129)), "keep;")).Succeeded);
        Assert.IsFalse((await service.PutAsync(userId, "line\u2028separator", "keep;")).Succeeded);
        Assert.IsFalse((await service.PutAsync(userId, "invalid-unicode", "keep;\ud800")).Succeeded);
        Assert.IsTrue((await service.PutAsync(userId, "Cafe\u0301", "keep;")).Succeeded);
        Assert.IsNotNull(await service.GetAsync(userId, "Café"));
    }

    [TestMethod]
    public async Task ScriptCountQuotaAllowsReplacementButRejectsAdditionalScripts()
    {
        await using var database = CreateDatabase();
        await database.Database.EnsureCreatedAsync();
        var userId = Guid.CreateVersion7();
        database.Users.Add(new UserDB
        {
            Id = userId,
            Username = "admin@mk8n.com",
            PasswordHash = "unused",
            Role = "User",
        });
        await database.SaveChangesAsync();
        var service = CreateService(database);

        Assert.IsTrue((await service.PutAsync(userId, "first", "keep;", 1)).Succeeded);
        var original = await service.GetAsync(userId, "first");
        Assert.IsNotNull(original);
        Assert.IsTrue((await service.SetActiveAsync(userId, "first")).Succeeded);
        Assert.IsTrue((await service.PutAsync(userId, "first", "discard;", 1)).Succeeded);
        var replacement = await service.GetAsync(userId, "first");
        Assert.IsNotNull(replacement);
        Assert.AreEqual(original.CreatedAt, replacement.CreatedAt);
        Assert.AreEqual("discard;", replacement.Content);
        Assert.IsTrue(replacement.IsActive);
        var rejected = await service.PutAsync(userId, "second", "keep;", 1);
        Assert.IsFalse(rejected.Succeeded);
        Assert.AreEqual("QUOTA/MAXSCRIPTS", rejected.ResponseCode);
        Assert.IsNull(await service.GetAsync(userId, "second"));
        Assert.HasCount(1, await service.ListAsync(userId));
    }

    [TestMethod]
    public async Task PutValidatesContentBeforeCheckingUnknownUser()
    {
        await using var database = CreateDatabase();
        await database.Database.EnsureCreatedAsync();
        var service = CreateService(database);
        var userId = Guid.CreateVersion7();

        var empty = await service.PutAsync(userId, "first", string.Empty);
        Assert.IsFalse(empty.Succeeded);
        Assert.AreEqual("QUOTA/MAXSIZE", empty.ResponseCode);

        var invalidUnicode = await service.PutAsync(userId, "first", "keep;\ud800");
        Assert.IsFalse(invalidUnicode.Succeeded);
        Assert.AreEqual("The script contains invalid Unicode.", invalidUnicode.Error);

        var unknownUser = await service.PutAsync(userId, "first", "keep;");
        Assert.IsFalse(unknownUser.Succeeded);
        Assert.AreEqual("The user does not exist.", unknownUser.Error);
        Assert.AreEqual(0, await database.SieveScripts.CountAsync());
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
