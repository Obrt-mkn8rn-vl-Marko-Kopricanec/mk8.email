using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        var service = new SieveScriptService(database);

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
        var service = new SieveScriptService(database);

        Assert.IsFalse((await service.PutAsync(userId, string.Empty, "keep;")).Succeeded);
        Assert.IsFalse((await service.PutAsync(userId, new string('x', 129), "keep;")).Succeeded);
        Assert.IsTrue((await service.PutAsync(userId, "Cafe\u0301", "keep;")).Succeeded);
        Assert.IsNotNull(await service.GetAsync(userId, "Café"));
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"sieve-scripts-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new EmailDbContext(options);
    }
}
