using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Data;

public class EmailDbContext(DbContextOptions<EmailDbContext> options) : DbContext(options)
{
    private bool _collectingJmapChanges;

    public DbSet<CompanyDB> Companies => Set<CompanyDB>();
    public DbSet<AddressDB> Addresses => Set<AddressDB>();
    public DbSet<UserDB> Users => Set<UserDB>();
    public DbSet<InboxDB> Inboxes => Set<InboxDB>();
    public DbSet<FolderDB> Folders => Set<FolderDB>();
    public DbSet<EmailDB> Emails => Set<EmailDB>();
    public DbSet<GlobalConfigDB> GlobalConfig => Set<GlobalConfigDB>();
    public DbSet<GlobalLimitsDB> GlobalLimits => Set<GlobalLimitsDB>();
    public DbSet<CompanyConfigDB> CompanyConfigs => Set<CompanyConfigDB>();
    public DbSet<CompanyLimitsDB> CompanyLimits => Set<CompanyLimitsDB>();
    public DbSet<ExpungedUidDB> ExpungedUids => Set<ExpungedUidDB>();
    public DbSet<MailQueueMessageDB> MailQueueMessages => Set<MailQueueMessageDB>();
    public DbSet<MailQueueRecipientDB> MailQueueRecipients => Set<MailQueueRecipientDB>();
    public DbSet<JmapChangeDB> JmapChanges => Set<JmapChangeDB>();
    public DbSet<JmapBlobDB> JmapBlobs => Set<JmapBlobDB>();
    public DbSet<JmapEmailSubmissionDB> JmapEmailSubmissions => Set<JmapEmailSubmissionDB>();
    public DbSet<JmapPushSubscriptionDB> JmapPushSubscriptions => Set<JmapPushSubscriptionDB>();
    public DbSet<JmapVacationResponseDB> JmapVacationResponses => Set<JmapVacationResponseDB>();
    public DbSet<JmapVacationReplyDB> JmapVacationReplies => Set<JmapVacationReplyDB>();
    public DbSet<JmapIdentityDB> JmapIdentities => Set<JmapIdentityDB>();
    public DbSet<DavCollectionDB> DavCollections => Set<DavCollectionDB>();
    public DbSet<DavResourceDB> DavResources => Set<DavResourceDB>();
    public DbSet<DavChangeDB> DavChanges => Set<DavChangeDB>();
    public DbSet<SieveScriptDB> SieveScripts => Set<SieveScriptDB>();

    private static readonly Guid GlobalConfigSeedId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid GlobalLimitsSeedId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareJmapChangesAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        await PrepareJmapChangesAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private async Task PrepareJmapChangesAsync(CancellationToken cancellationToken)
    {
        if (_collectingJmapChanges)
            return;

        _collectingJmapChanges = true;
        try
        {
            await JmapChangeCollector.CollectAsync(this, cancellationToken);
        }
        finally
        {
            _collectingJmapChanges = false;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CompanyDB>(entity =>
        {
            entity.HasIndex(c => c.Name).IsUnique();
        });

        modelBuilder.Entity<AddressDB>(entity =>
        {
            entity.HasIndex(a => a.Domain).IsUnique();

            entity.HasOne(a => a.Company)
                  .WithMany(c => c.Addresses)
                  .HasForeignKey(a => a.CompanyId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserDB>(entity =>
        {
            entity.HasIndex(u => u.Username).IsUnique();

            entity.HasOne(u => u.Company)
                  .WithMany(c => c.Users)
                  .HasForeignKey(u => u.CompanyId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<InboxDB>(entity =>
        {
            entity.HasIndex(i => new { i.AddressId, i.Name }).IsUnique();

            entity.HasOne(i => i.Address)
                  .WithMany(a => a.Inboxes)
                  .HasForeignKey(i => i.AddressId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(i => i.Owner)
                  .WithMany()
                  .HasForeignKey(i => i.OwnerId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(i => i.AliasForInbox)
                  .WithMany()
                  .HasForeignKey(i => i.AliasForInboxId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<FolderDB>(entity =>
        {
            entity.HasIndex(f => new { f.InboxId, f.Name }).IsUnique();
            entity.HasIndex(f => new { f.InboxId, f.MailboxId }).IsUnique();
            entity.HasIndex(f => new { f.InboxId, f.JmapRole })
                .IsUnique()
                .HasFilter("jmap_role IS NOT NULL");

            entity.HasOne(f => f.Inbox)
                  .WithMany(i => i.Folders)
                  .HasForeignKey(f => f.InboxId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmailDB>(entity =>
        {
            entity.HasIndex(e => e.ReceivedAt);
            entity.HasIndex(e => new { e.FolderId, e.Uid }).IsUnique();
            entity.HasIndex(e => new { e.FolderId, e.ModSeq });
            entity.HasIndex(e => e.MessageId);
            entity.HasIndex(e => e.EmailObjectId);
            entity.HasIndex(e => e.QueueDeliveryId).IsUnique();

            entity.HasOne(e => e.Folder)
                  .WithMany(f => f.Emails)
                  .HasForeignKey(e => e.FolderId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MailQueueMessageDB>(entity =>
        {
            entity.HasIndex(message => new { message.State, message.NextAttemptAt });
            entity.HasIndex(message => message.ReceivedAt);
        });

        modelBuilder.Entity<MailQueueRecipientDB>(entity =>
        {
            entity.HasIndex(recipient => new { recipient.MessageId, recipient.State });

            entity.HasOne(recipient => recipient.Message)
                  .WithMany(message => message.Recipients)
                  .HasForeignKey(recipient => recipient.MessageId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SieveScriptDB>(entity =>
        {
            entity.HasIndex(script => new { script.UserId, script.Name }).IsUnique();
            entity.HasIndex(script => script.UserId)
                .IsUnique()
                .HasFilter("is_active");

            entity.HasOne(script => script.User)
                .WithMany()
                .HasForeignKey(script => script.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JmapChangeDB>(entity =>
        {
            entity.HasIndex(change => new { change.AccountId, change.DataType, change.Sequence });
            entity.HasIndex(change => change.ChangedAt);
            entity.HasIndex(change => change.AccountId)
                .IsUnique()
                .HasFilter("data_type = '_Account'");
        });

        modelBuilder.Entity<JmapBlobDB>(entity =>
        {
            entity.HasIndex(blob => blob.BlobId).IsUnique();
            entity.HasIndex(blob => new { blob.AccountId, blob.ExpiresAt });
        });

        modelBuilder.Entity<JmapEmailSubmissionDB>(entity =>
        {
            entity.HasIndex(submission => submission.SubmissionObjectId).IsUnique();
            entity.HasIndex(submission => submission.QueueId).IsUnique();
            entity.HasIndex(submission => new { submission.AccountId, submission.CreatedAt });
        });

        modelBuilder.Entity<JmapPushSubscriptionDB>(entity =>
        {
            entity.HasIndex(subscription => subscription.SubscriptionObjectId).IsUnique();
            entity.HasIndex(subscription => new { subscription.UserId, subscription.DeviceClientId });
            entity.HasIndex(subscription => subscription.ExpiresAt);
        });

        modelBuilder.Entity<JmapVacationResponseDB>();

        modelBuilder.Entity<JmapVacationReplyDB>(entity =>
        {
            entity.HasIndex(reply => new { reply.AccountId, reply.SenderAddress }).IsUnique();
            entity.HasIndex(reply => reply.LastDeliveryId).IsUnique();
            entity.HasIndex(reply => reply.LastSentAt);
        });

        modelBuilder.Entity<JmapIdentityDB>(entity =>
        {
            entity.HasIndex(identity => identity.IdentityObjectId).IsUnique();
            entity.HasIndex(identity => new { identity.AccountId, identity.Email });
        });

        modelBuilder.Entity<DavCollectionDB>(entity =>
        {
            entity.HasIndex(collection => new
            {
                collection.UserId,
                collection.CollectionType,
                collection.Slug,
            }).IsUnique();

            entity.HasOne(collection => collection.User)
                .WithMany()
                .HasForeignKey(collection => collection.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DavResourceDB>(entity =>
        {
            entity.HasIndex(resource => new { resource.CollectionId, resource.ResourceName })
                .IsUnique();
            entity.HasIndex(resource => new { resource.CollectionId, resource.Uid })
                .IsUnique();
            entity.HasIndex(resource => new { resource.CollectionId, resource.ChangeSequence });

            entity.HasOne(resource => resource.Collection)
                .WithMany(collection => collection.Resources)
                .HasForeignKey(resource => resource.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DavChangeDB>(entity =>
        {
            entity.HasIndex(change => new { change.CollectionId, change.Sequence }).IsUnique();
            entity.HasIndex(change => change.ChangedAt);

            entity.HasOne(change => change.Collection)
                .WithMany(collection => collection.Changes)
                .HasForeignKey(change => change.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExpungedUidDB>(entity =>
        {
            entity.HasIndex(eu => new { eu.FolderId, eu.Uid });
            entity.HasIndex(eu => new { eu.FolderId, eu.ModSeq });

            entity.HasOne(eu => eu.Folder)
                  .WithMany(f => f.ExpungedUids)
                  .HasForeignKey(eu => eu.FolderId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CompanyConfigDB>(entity =>
        {
            entity.HasIndex(cc => cc.CompanyId).IsUnique();

            entity.HasOne(cc => cc.Company)
                  .WithOne(c => c.Config)
                  .HasForeignKey<CompanyConfigDB>(cc => cc.CompanyId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CompanyLimitsDB>(entity =>
        {
            entity.HasIndex(cl => cl.CompanyId).IsUnique();

            entity.HasOne(cl => cl.Company)
                  .WithOne(c => c.Limits)
                  .HasForeignKey<CompanyLimitsDB>(cl => cl.CompanyId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GlobalConfigDB>().HasData(new GlobalConfigDB
        {
            Id = GlobalConfigSeedId,
            AllowRegistration = false,
            SmtpHostname = "localhost",
            SmtpPort = 25,
            SmtpSubmissionPort = 587,
            SmtpImplicitTlsPort = 465,
            EnableSmtp = true,
            EnableSubmission = false,
            EnableImplicitTls = false,
            EnableStartTls = false,
            RequireTls = false,
            PasswordHashScheme = "BLF-CRYPT",
            RequireAuth = true,
            MaxMessageSizeBytes = 10 * 1024 * 1024,
            MaxRecipientsPerMessage = 100,
            ConnectionTimeoutSeconds = 300,
            MaxConnectionsPerIp = 10,
            AllowRelay = false,
            EnableImap = true,
            ImapPort = 143,
            EnableImapImplicitTls = false,
            ImapImplicitTlsPort = 993,
        });

        modelBuilder.Entity<GlobalLimitsDB>().HasData(new GlobalLimitsDB
        {
            Id = GlobalLimitsSeedId,
            DefaultMaxDomainsPerCompany = 0,
            DefaultMaxInboxesPerCompany = 0,
            DefaultMaxInboxesPerDomain = 0,
        });
    }
}
