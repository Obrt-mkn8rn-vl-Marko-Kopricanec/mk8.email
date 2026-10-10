using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Storage;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Storage;
using Npgsql;

namespace mk8.email.Messaging.Tests;

internal sealed class NativeSmtpDurabilityFixture : IAsyncDisposable
{
    internal static readonly Guid InboxId = new(0x85960c01u, 0x1f11, 0x43fc, 0xbf, 0x94, 0x11, 0x50, 0x92, 0xbe, 0x93, 0xc0);
    internal static readonly Guid FolderId = new(0x95960c01u, 0x1f11, 0x43fc, 0xbf, 0x94, 0x11, 0x50, 0x92, 0xbe, 0x93, 0xc0);
    private readonly PostgresTestDatabase _database;
    private readonly string _blobConnection;
    private readonly BlobContainerClient _container;
    private ServiceProvider? _workerProvider;
    private ServiceProvider? _gatewayProvider;
    private ApplicationRequestWorker? _worker;
    private NativeSmtpConnection? _native;
    private readonly EnvironmentConfig _environment;
    public NativeSmtpJournalControl? JournalControl { get; private set; }
    private ServiceProvider WorkerProvider => _workerProvider ?? throw new InvalidOperationException("The Worker fixture is not initialized.");
    public PostgresApplicationBus Bus => WorkerProvider.GetRequiredService<PostgresApplicationBus>();
    public PostgresGatewayTrafficJournal Journal => WorkerProvider.GetRequiredService<PostgresGatewayTrafficJournal>();
    public string ConnectionString => _database.ConnectionString;
    public NativeSmtpConnection Native => _native ?? throw new InvalidOperationException("The native connection is not initialized.");

    private NativeSmtpDurabilityFixture(PostgresTestDatabase database, string blobConnection)
    {
        _database = database;
        _blobConnection = blobConnection;
        _container = new BlobServiceClient(blobConnection).GetBlobContainerClient($"mk8-native-{Guid.NewGuid():N}");
        var configured = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        _environment = new EnvironmentConfig
        {
            Database = new DatabaseConfig
            {
                Host = configured.Host ?? throw new InvalidOperationException("The test PostgreSQL host is missing."),
                Port = configured.Port,
                Name = database.DatabaseName,
                Username = configured.Username ?? throw new InvalidOperationException("The test PostgreSQL user is missing."),
                Password = configured.Password ?? string.Empty,
            },
            Smtp = new SmtpConfig { Hostname = "email.example.test", RequireAuth = true, AllowRelay = true },
            Imap = new ImapConfig { EnableImap = false },
            Pop3 = new Pop3Config { EnablePop3 = false },
            Jmap = new JmapConfig { EnableJmap = false },
            OAuth = new OAuthConfig { EnableOAuth = false },
            Limits = new LimitsConfig { MaxMessageSizeBytes = 65_536 },
        };
    }

    public static async Task<NativeSmtpDurabilityFixture> CreateAsync(NativeSmtpJournalMode mode)
    {
        var blob = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(blob)) throw new AssertInconclusiveException("An owned Azure-compatible development endpoint is required.");
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false)
            ?? throw new AssertInconclusiveException("An owned PostgreSQL development endpoint is required.");
        NativeSmtpDurabilityFixture? fixture = null;
        try
        {
            fixture = new NativeSmtpDurabilityFixture(database, blob);
            await fixture.InitializeAsync(mode).ConfigureAwait(false);
            return fixture;
        }
        // Construction failure must retain its original fault through retirement.
#pragma warning disable CA1031
        catch (Exception original)
#pragma warning restore CA1031
        {
            try
            {
                if (fixture is null) await database.DisposeAsync().ConfigureAwait(false);
                else await fixture.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception cleanup)
#pragma warning restore CA1031
            {
                throw new AggregateException(original, cleanup);
            }
            throw;
        }
    }

    private ServiceProvider BuildWorkerProvider()
    {
        var options = new PostgresMessagingOptions
        {
            MaxPayloadBytes = 2 * 1024 * 1024,
            InlinePayloadThresholdBytes = 128,
            NotificationFallbackInterval = TimeSpan.FromMilliseconds(100),
        };
        var services = new ServiceCollection().AddLogging().AddSingleton(_environment);
        services.AddDbContext<EmailDbContext>(configuration => configuration.UseNpgsql(ConnectionString));
        services.AddApplication().AddMailApplicationWorker();
        services.AddSingleton(_ => NpgsqlDataSource.Create(ConnectionString));
        services.AddSingleton(_ => AesGcmPayloadProtectorTests.CreateProtector("test", "native-inbound-public-test-key"));
        services.AddSingleton<ILargeObjectStore>(_ => new AzureBlobLargeObjectStore(new BlobServiceClient(_blobConnection),
            new AzureBlobLargeObjectStoreOptions { ContainerName = _container.Name, CreateContainerIfMissing = true }));
        services.AddSingleton<IMailScanner>(new CleanScanner());
        services.AddSingleton<IOutboundMailRelay>(new RefusingOutboundRelay());
        services.AddSingleton(provider => new PostgresApplicationBus(provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<AesGcmPayloadProtector>(), options,
            largeObjectStore: provider.GetRequiredService<ILargeObjectStore>()));
        services.AddSingleton(provider => new PostgresGatewayTrafficJournal(provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<AesGcmPayloadProtector>(), options, provider.GetRequiredService<ILargeObjectStore>()));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private async Task InitializeAsync(NativeSmtpJournalMode mode)
    {
        _workerProvider = BuildWorkerProvider();
        await SeedAsync().ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(WorkerProvider.GetRequiredService<NpgsqlDataSource>()).ConfigureAwait(false);
        _worker = new ApplicationRequestWorker(Bus, WorkerProvider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("native-inbound@test", TimeSpan.FromSeconds(30)), NullLogger<ApplicationRequestWorker>.Instance);
        await _worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        JournalControl = new NativeSmtpJournalControl(Journal, mode);
        var transport = new GatewayApplicationTransport(Bus, JournalControl,
            new GatewayApplicationOptions("native-inbound-test", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5)),
            new PostgresApplicationTransportControl(WorkerProvider.GetRequiredService<NpgsqlDataSource>()));
        _gatewayProvider = new ServiceCollection().AddSingleton<ISmtpApplicationService>(new GatewaySmtpApplicationService(transport))
            .BuildServiceProvider();
        _native = new NativeSmtpConnection(_gatewayProvider.GetRequiredService<IServiceScopeFactory>(), _environment, JournalControl);
    }

    private async Task SeedAsync()
    {
        var scope = WorkerProvider.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
        var company = new CompanyDB { Id = Guid.CreateVersion7(), Name = "Native inbound fixture", IsActive = true };
        var address = new AddressDB { Id = Guid.CreateVersion7(), Domain = "example.test", Company = company, IsActive = true };
        var user = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = "receiver@example.test",
            PasswordHash = "unused-anonymous-fixture",
            Company = company,
            IsActive = true,
        };
        var inbox = new InboxDB { Id = InboxId, Name = "receiver", Address = address, Owner = user };
        inbox.Folders.Add(new FolderDB { Id = FolderId, Name = DefaultFolders.Inbox, JmapRole = "inbox" });
        await database.Inboxes.AddAsync(inbox).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<MailQueueLargeObjectMigrationService>().MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<MailboxMessageLargeObjectMigrationService>().MigrateAsync().ConfigureAwait(false);
    }

    public EmailDbContext OpenContext() => new(new DbContextOptionsBuilder<EmailDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task<string> ReadQueuedAsync(MailQueueMessageDB message, CancellationToken cancellationToken)
    {
        var scope = WorkerProvider.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        return await scope.ServiceProvider.GetRequiredService<MailQueueContentService>().ReadAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadMailboxAsync(EmailDB message, CancellationToken cancellationToken)
    {
        var scope = WorkerProvider.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        return await scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>().ReadAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeliverAfterReconstructionAsync(CancellationToken cancellationToken)
    {
        await RetireRolesAsync().ConfigureAwait(false);
        var previous = WorkerProvider;
        _workerProvider = null;
        await previous.DisposeAsync().ConfigureAwait(false);
        _workerProvider = BuildWorkerProvider();
        var queue = WorkerProvider.GetRequiredService<MailQueueWorker>();
        Assert.IsTrue(await queue.ProcessNextAsync(cancellationToken).ConfigureAwait(false));
        Assert.IsFalse(await queue.ProcessNextAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task RetireRolesAsync()
    {
        var errors = new List<Exception>();
        JournalControl?.Release();
        if (_native is not null)
        {
            try { await _native.DisposeAsync().ConfigureAwait(false); }
            // Attempt Worker retirement even when the native join fails.
#pragma warning disable CA1031
            catch (Exception error)
#pragma warning restore CA1031
            { errors.Add(error); }
            if (_native.Completing.IsCompleted) _native = null;
        }
        if (_worker is not null)
        {
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var observation = new CancellationTokenSource();
            var stopping = _worker.StopAsync(stopDeadline.Token);
            // Join the actual BackgroundService body as well as this fixture's stop request.
#pragma warning disable VSTHRD003
            await AttemptRetirementAsync(() => GatewayEwsRouteTests.ObserveWriterCleanupAsync(
                stopping, _worker.ExecuteTask, observation, originalFailure: null), errors).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            if (_worker.ExecuteTask is null or { IsCompleted: true })
            {
                _worker.Dispose();
                _worker = null;
            }
        }
        if (_native is not null || _worker is not null)
            errors.Add(new TimeoutException("Actual native/Worker work remains active; dependencies are retained."));
        else if (_gatewayProvider is not null)
        {
            try { await _gatewayProvider.DisposeAsync().ConfigureAwait(false); }
#pragma warning disable CA1031
            catch (Exception error)
#pragma warning restore CA1031
            { errors.Add(error); }
            _gatewayProvider = null;
        }
        if (errors.Count != 0) throw new AggregateException("Native fixture retirement failed.", errors);
    }

    private static async Task AttemptRetirementAsync(Func<Task> retire, List<Exception> errors)
    {
        try { await retire().ConfigureAwait(false); }
        // One failed retirement stage must not skip cancellation/observation of the other role.
#pragma warning disable CA1031
        catch (Exception error)
#pragma warning restore CA1031
        { errors.Add(error); }
    }

    public async ValueTask DisposeAsync()
    {
        await RetireRolesAsync().ConfigureAwait(false);
        if (_workerProvider is not null)
        {
            await _workerProvider.DisposeAsync().ConfigureAwait(false);
            _workerProvider = null;
        }
        try { await _container.DeleteIfExistsAsync().ConfigureAwait(false); }
        finally { await _database.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class CleanScanner : IMailScanner
    {
        public Task<MailScanResult> ScanAsync(MailScanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailScanResult("no action", 0, 5, new HashSet<string>(StringComparer.Ordinal), string.Empty,
                IsMalware: false, IsTemporaryFailure: false));
    }

    private sealed class RefusingOutboundRelay : IOutboundMailRelay
    {
        public Task<OutboundDeliveryResult> RelayAsync(string sender, string recipient, string rawMessage,
            OutboundMailOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This local inbound proof must not deliver to an external SMTP host.");
    }
}
