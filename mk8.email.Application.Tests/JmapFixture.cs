using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

internal sealed class JmapFixture : IAsyncDisposable
{
    private JmapFixture(
        ServiceProvider services,
        EnvironmentConfig configuration,
        AuthenticatedMailUser user,
        Guid inboxId,
        Guid inboxFolderId,
        Guid draftsFolderId,
        Guid sentFolderId)
    {
        Services = services;
        Configuration = configuration;
        User = user;
        InboxId = inboxId;
        InboxFolderId = inboxFolderId;
        DraftsFolderId = draftsFolderId;
        SentFolderId = sentFolderId;
    }

    public ServiceProvider Services { get; }
    public EnvironmentConfig Configuration { get; }
    public AuthenticatedMailUser User { get; }
    public Guid InboxId { get; }
    public Guid InboxFolderId { get; }
    public Guid DraftsFolderId { get; }
    public Guid SentFolderId { get; }
    public string AccountId => JmapId.Account(InboxId);
    public string InboxMailboxId => JmapId.Mailbox(InboxFolderId);
    public string DraftsMailboxId => JmapId.Mailbox(DraftsFolderId);

    public static async Task<JmapFixture> CreateAsync(long? maximumUnreferencedBlobBytes = null)
    {
        var blobLimit = maximumUnreferencedBlobBytes ?? 100_000_000;
        var objectSizeLimit = maximumUnreferencedBlobBytes is null
            ? 10 * 1024 * 1024
            : checked((int)blobLimit);
        var configuration = new EnvironmentConfig
        {
            Smtp = new SmtpConfig
            {
                Hostname = "email.mk8n.com",
                AllowRelay = false,
            },
            Jmap = new JmapConfig
            {
                EnableJmap = true,
                IsDefault = true,
                PublicBaseUrl = "https://email.mk8n.com",
                MaxUploadSizeBytes = objectSizeLimit,
                MaxUnreferencedBlobBytesPerAccount = blobLimit,
            },
            Limits = new LimitsConfig { MaxMessageSizeBytes = objectSizeLimit },
        };
        var services = new ServiceCollection();
        var databaseName = $"jmap-{Guid.NewGuid():N}";
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddDbContext<EmailDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddJmapProtocol();
        var provider = services.BuildServiceProvider();

        var userId = Guid.CreateVersion7();
        var inboxId = Guid.CreateVersion7();
        var inboxFolderId = Guid.CreateVersion7();
        var draftsFolderId = Guid.CreateVersion7();
        var sentFolderId = Guid.CreateVersion7();
        const string username = "user@mk8n.com";
        using (var scope = provider.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "JMAP Test",
            };
            var address = new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "mk8n.com",
                Company = company,
                IsActive = true,
            };
            var user = new UserDB
            {
                Id = userId,
                Username = username,
                PasswordHash = "unused",
                Role = "User",
                Company = company,
            };
            var inbox = new InboxDB
            {
                Id = inboxId,
                Name = "user",
                Address = address,
                Owner = user,
            };
            inbox.Folders.Add(CreateFolder(inboxFolderId, inboxId, "INBOX", "inbox"));
            inbox.Folders.Add(CreateFolder(draftsFolderId, inboxId, "Drafts", "drafts"));
            inbox.Folders.Add(CreateFolder(sentFolderId, inboxId, "Sent", "sent"));
            inbox.Folders.Add(CreateFolder(Guid.CreateVersion7(), inboxId, "Trash", "trash"));
            inbox.Folders.Add(CreateFolder(Guid.CreateVersion7(), inboxId, "Spam", "junk"));
            database.Inboxes.Add(inbox);
            await database.SaveChangesAsync();
        }

        return new JmapFixture(
            provider,
            configuration,
            new AuthenticatedMailUser(userId, username),
            inboxId,
            inboxFolderId,
            draftsFolderId,
            sentFolderId);
    }

    public async Task<JsonObject> InvokeAsync(string requestJson)
    {
        return await InvokeAsync(JsonNode.Parse(requestJson));
    }

    public async Task<JsonObject> InvokeAsync(JsonNode? request)
    {
        using var scope = Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        return await processor.ProcessAsync(request, User);
    }

    public async Task<string> StoreBlobAsync(byte[] content, string contentType = "message/rfc822")
    {
        using var scope = Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .StoreAsync(InboxId, content, contentType, null, CancellationToken.None);
        return stored.BlobId;
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    private static FolderDB CreateFolder(Guid id, Guid inboxId, string name, string role) => new()
    {
        Id = id,
        InboxId = inboxId,
        Name = name,
        JmapRole = role,
        IsSubscribed = true,
        UidValidity = 1,
        NextUid = 1,
    };
}
