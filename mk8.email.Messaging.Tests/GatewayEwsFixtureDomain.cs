using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;
using mk8.email.Hosting;

namespace mk8.email.Messaging.Tests;

// Only credential issuance is controlled. Folder/profile/receipt operations execute
// the production Application dispatcher and domain services against PostgreSQL.
internal static class GatewayEwsFixtureDomain
{
    internal static readonly Guid AccountId = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    internal static readonly Guid OwnerId = new("1238f53b-3efa-4a59-8602-e215c0df1e35");
    internal static readonly Guid InboxId = new("1338f53b-3efa-4a59-8602-e215c0df1e35");
    internal static readonly Guid ChildId = new("1438f53b-3efa-4a59-8602-e215c0df1e35");
    internal static readonly Guid GrandchildId = new("1538f53b-3efa-4a59-8602-e215c0df1e35");
    internal static readonly Guid ForeignAccountId = new("1638f53b-3efa-4a59-8602-e215c0df1e35");
    internal static readonly Guid ForeignFolderId = new("1738f53b-3efa-4a59-8602-e215c0df1e35");

    public static void Configure(IServiceCollection services, string connection, ILargeObjectStore objects,
        AesGcmPayloadProtector protector, EnvironmentConfig environment, DbCommandInterceptor? interceptor = null)
    {
        services.AddLogging().AddSingleton(environment).AddSingleton(objects)
            .AddSingleton<IStoredContentProtector>(new MessagingStoredContentProtector(protector))
            .AddSingleton<IMailAuthenticator>(new Authenticator());
        services.AddDbContext<EmailDbContext>(options =>
        {
            options.UseNpgsql(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
        });
        services.AddJmapApplication();
    }

    public static async Task SeedAsync(string connection, int additionalFolders)
    {
        var database = new EmailDbContext(new DbContextOptionsBuilder<EmailDbContext>().UseNpgsql(connection).Options);
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
        var company = new CompanyDB { Id = Guid.CreateVersion7(), Name = "EWS fixture" };
        var address = new AddressDB { Id = Guid.CreateVersion7(), Domain = "example.test", Company = company, IsActive = true };
        var owner = new UserDB { Id = OwnerId, Username = "owner@example.test", PasswordHash = "unused", Company = company };
        var foreign = new UserDB { Id = Guid.CreateVersion7(), Username = "foreign@example.test", PasswordHash = "unused", Company = company };
        var inbox = new InboxDB { Id = AccountId, Name = "owner", Address = address, Owner = owner };
        var other = new InboxDB { Id = ForeignAccountId, Name = "foreign", Address = address, Owner = foreign };
        inbox.Folders.Add(Folder(InboxId, "INBOX", "inbox"));
        inbox.Folders.Add(Folder(ChildId, "INBOX/A & B", null));
        inbox.Folders.Add(Folder(GrandchildId, "INBOX/A & B/Deep", null));
        inbox.Folders.Add(Folder(Guid.CreateVersion7(), "Sent", "sent"));
        inbox.Folders.Add(Folder(Guid.CreateVersion7(), "Drafts", "drafts"));
        inbox.Folders.Add(Folder(Guid.CreateVersion7(), "Trash", "trash"));
        inbox.Folders.Add(Folder(Guid.CreateVersion7(), "Spam", "junk"));
        for (var index = 0; index < additionalFolders; index++)
            inbox.Folders.Add(Folder(Guid.CreateVersion7(), $"Extra-{index:D4}", null));
        other.Folders.Add(Folder(ForeignFolderId, "PRIVATE FOREIGN NAME", null));
        await database.Inboxes.AddRangeAsync(inbox, other).ConfigureAwait(false);
        await database.Emails.AddRangeAsync(
            new EmailDB { Id = Guid.CreateVersion7(), FolderId = InboxId, Uid = 1, IsRead = false },
            new EmailDB { Id = Guid.CreateVersion7(), FolderId = InboxId, Uid = 2, IsRead = true },
            new EmailDB { Id = Guid.CreateVersion7(), FolderId = InboxId, Uid = 3, IsDeleted = true },
            new EmailDB { Id = Guid.CreateVersion7(), FolderId = InboxId, Uid = 4, IsDraft = true }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
    }

    private static FolderDB Folder(Guid id, string name, string? role) => new() { Id = id, Name = name, JmapRole = role };

    private sealed class Authenticator : IMailAuthenticator
    {
        public Task<AuthenticatedMailUser?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthenticatedMailUser?>(string.Equals(password, "test-protocol-secret", StringComparison.Ordinal)
                && username.Equals("owner@example.test", StringComparison.OrdinalIgnoreCase)
                    ? new(OwnerId, "owner@example.test") : null);
    }
}
