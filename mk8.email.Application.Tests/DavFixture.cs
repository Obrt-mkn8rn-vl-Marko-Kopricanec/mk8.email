using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Dav;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Models;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Gateway.Protocols.Dav;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Contracts.Messaging;
using mk8.email.Jmap;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

internal sealed class DavFixture : IAsyncDisposable
{
    private const string Username = "dav.user@tenant.example.test";
    private const string AttendeeUsername = "dav.attendee@tenant.example.test";
    private const string OutsiderUsername = "dav.outsider@other.example";
    private const string Password = "correct horse battery staple";
    private readonly WebApplication webApplication;
    private readonly CapturingMailSubmissionQueue queuedSubmissions;

    private DavFixture(
        WebApplication application,
        HttpClient client,
        Guid userId,
        Guid inboxId,
        Guid attendeeUserId,
        Guid outsiderUserId,
        CapturingMailSubmissionQueue submissionQueue)
    {
        webApplication = application;
        queuedSubmissions = submissionQueue;
        Client = client;
        UserId = userId;
        InboxId = inboxId;
        AttendeeUserId = attendeeUserId;
        OutsiderUserId = outsiderUserId;
    }

    public HttpClient Client { get; }
    public IServiceProvider Services => webApplication.Services;
    public Guid UserId { get; }
    public Guid InboxId { get; }
    public Guid AttendeeUserId { get; }
    public Guid OutsiderUserId { get; }
    public static string PrimaryAddress => Username;
    public string AccountId => JmapId.Account(InboxId);
    public static string AttendeeAddress => AttendeeUsername;
    public string PrincipalPath => $"/dav/principals/{UserId:N}/";
    public string AttendeePrincipalPath => $"/dav/principals/{AttendeeUserId:N}/";
    public string OutsiderPrincipalPath => $"/dav/principals/{OutsiderUserId:N}/";
    public string CalendarHomePath => $"/dav/calendars/{UserId:N}/";
    public string AddressBookHomePath => $"/dav/addressbooks/{UserId:N}/";
    public string SchedulingInboxPath => CalendarHomePath + "schedule-inbox/";
    public string SchedulingOutboxPath => CalendarHomePath + "schedule-outbox/";
    public string AttendeeCalendarHomePath => $"/dav/calendars/{AttendeeUserId:N}/";
    public string AttendeeAddressBookHomePath => $"/dav/addressbooks/{AttendeeUserId:N}/";
    public string AttendeeSchedulingInboxPath => AttendeeCalendarHomePath + "schedule-inbox/";
    public IReadOnlyList<MailSubmission> QueuedSubmissions => queuedSubmissions.Submissions;


    public static async Task<DavFixture> CreateAsync()
    {
        var application = CreateApplication(out var submissionQueue);
        var userId = Guid.CreateVersion7();
        var inboxId = Guid.CreateVersion7();
        var attendeeUserId = Guid.CreateVersion7();
        var outsiderUserId = Guid.CreateVersion7();

        HttpClient? client = null;
        try
        {
            await SeedAsync(application, userId, inboxId, attendeeUserId, outsiderUserId).ConfigureAwait(false);
            await VerifyAuthenticationAsync(application).ConfigureAwait(false);
            await application.StartAsync().ConfigureAwait(false);
            var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            client = CreateClient(AssertExactlyOne(addresses));
            return new DavFixture(
                application,
                client,
                userId,
                inboxId,
                attendeeUserId,
                outsiderUserId,
                submissionQueue);
        }
        catch
        {
            client?.Dispose();
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static WebApplication CreateApplication(out CapturingMailSubmissionQueue submissionQueue)
    {
        var configuration = new EnvironmentConfig
        {
            Smtp = new SmtpConfig
            {
                Hostname = "email.tenant.example.test",
                AllowRelay = true,
            },
            Dav = new DavConfig
            {
                EnableDav = true,
                MaxResourceSizeBytes = 1_048_576,
                MaxCollectionsPerUser = 20,
                MaxResourcesPerCollection = 1_000,
            },
            Jmap = new JmapConfig
            {
                EnableJmap = true,
                IsDefault = true,
                PublicBaseUrl = "https://email.tenant.example.test",
            },
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton<InMemoryLargeObjectStore>();
        builder.Services.AddSingleton<ILargeObjectStore>(provider =>
            provider.GetRequiredService<InMemoryLargeObjectStore>());
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = $"dav-{Guid.NewGuid():N}";
        builder.Services.AddDbContext<EmailDbContext>(options =>
            options.UseInMemoryDatabase(databaseName, databaseRoot));
        builder.Services.AddScoped<IMailAuthenticator, MailAuthenticator>();
        submissionQueue = new CapturingMailSubmissionQueue();
        builder.Services.AddSingleton<IMailSubmissionQueue>(submissionQueue);
        builder.Services.AddDavProtocol();
        builder.Services.AddJmapApplication();
        builder.Services.AddSingleton<IGatewayJmapClient, InProcessGatewayJmapClient>();
        builder.Services.AddSingleton<GatewayJmapBatchLimiter>();
        builder.Services.AddScoped<GatewayDavStore>();
        builder.Services.AddSingleton<IGatewayApplicationTransport, InProcessDavTransport>();

        var application = builder.Build();
        GatewayDavEndpointRouteBuilderExtensions.MapDavEndpoints(application);
        application.MapJmapEndpoints();

        return application;

    }

    private static async Task SeedAsync(WebApplication application, Guid userId, Guid inboxId, Guid attendeeUserId, Guid outsiderUserId)
    {
        using (var scope = application.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "DAV Protocol Test",
            };
            var primaryAddress = new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "tenant.example.test",
                Company = company,
                IsActive = true,
            };
            var primaryUser = new UserDB
            {
                Id = userId,
                Username = Username,
                PasswordHash = PasswordHasher.Hash(Password),
                Role = "User",
                Company = company,
            };
            await database.Addresses.AddAsync(primaryAddress).ConfigureAwait(false);
            await database.Users.AddAsync(primaryUser).ConfigureAwait(false);
            await database.Inboxes.AddAsync(new InboxDB
            {
                Id = inboxId,
                Name = "dav.user",
                Address = primaryAddress,
                Owner = primaryUser,
            }).ConfigureAwait(false);
            await database.Users.AddAsync(new UserDB
            {
                Id = attendeeUserId,
                Username = AttendeeUsername,
                PasswordHash = PasswordHasher.Hash(Password),
                Role = "User",
                Company = company,
            }).ConfigureAwait(false);
            await SeedOutsiderAsync(database, outsiderUserId).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

    }

    private static async Task SeedOutsiderAsync(EmailDbContext database, Guid outsiderUserId)
    {
        var outsiderCompany = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "Other DAV Tenant",
        };
        await database.Addresses.AddAsync(new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "other.example",
            Company = outsiderCompany,
            IsActive = true,
        }).ConfigureAwait(false);
        await database.Users.AddAsync(new UserDB
        {
            Id = outsiderUserId,
            Username = OutsiderUsername,
            PasswordHash = PasswordHasher.Hash(Password),
            Role = "User",
            Company = outsiderCompany,
        }).ConfigureAwait(false);

    }

    private static async Task VerifyAuthenticationAsync(WebApplication application)
    {
        using (var verificationScope = application.Services.CreateScope())
        {
            var seededAuthentication = await verificationScope.ServiceProvider
                .GetRequiredService<IMailAuthenticator>()
                .AuthenticateAsync(Username, Password).ConfigureAwait(false);
            Assert.IsNotNull(seededAuthentication, "The DAV fixture account must authenticate from a fresh service scope.");
        }


    }

    private static HttpClient CreateClient(string address)
    {
        SocketsHttpHandler? handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        try
        {
            var client = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(15),
            };
            handler = null;
            return client;
        }
        finally
        {
            handler?.Dispose();
        }
    }

    public async Task<JsonObject> SendJmapAsync(JsonObject request)
    {
        using var response = await SendAsync(
            "POST",
            "/jmap/api",
            request.ToJsonString(JmapJson.SerializerOptions),
            "application/json").ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsInstanceOfType<JsonObject>(
            JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)));
    }

    public async Task<string> StoreBlobAsync(byte[] content, string contentType)
    {
        using var scope = Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .StoreAsync(InboxId, content, contentType, null, CancellationToken.None).ConfigureAwait(false);
        return stored.BlobId;
    }

    public Task<HttpResponseMessage> SendAsync(
        string method,
        string path,
        string? body = null,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? headers = null,
        bool authenticate = true) => SendAsAsync(
        Username,
        method,
        path,
        body,
        contentType,
        headers,
        authenticate);

    public Task<HttpResponseMessage> SendAsAttendeeAsync(
        string method,
        string path,
        string? body = null,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? headers = null) => SendAsAsync(
        AttendeeUsername,
        method,
        path,
        body,
        contentType,
        headers,
        authenticate: true);

    private async Task<HttpResponseMessage> SendAsAsync(
        string username,
        string method,
        string path,
        string? body,
        string? contentType,
        IReadOnlyDictionary<string, string>? headers,
        bool authenticate)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (authenticate)
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{username}:{Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        if (body is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                contentType ?? "application/xml; charset=utf-8");
        }
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return await Client.SendAsync(request).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await webApplication.StopAsync().ConfigureAwait(false);
        await webApplication.DisposeAsync().ConfigureAwait(false);
    }

    private static string AssertExactlyOne(ICollection<string>? values)
    {
        Assert.IsNotNull(values);
        Assert.AreEqual(1, values.Count);
        return values.Single();
    }

    private sealed class CapturingMailSubmissionQueue : IMailSubmissionQueue
    {
        private readonly List<MailSubmission> submissions = [];

        public IReadOnlyList<MailSubmission> Submissions
        {
            get
            {
                lock (submissions)
                    return submissions.ToArray();
            }
        }

        public Task<Guid> EnqueueAsync(
            MailSubmission submission,
            CancellationToken cancellationToken = default)
        {
            lock (submissions)
                submissions.Add(submission);
            return Task.FromResult(submission.QueueId);
        }
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "This fixture implementation is activated through the test service provider's registered generic interface mapping.")]
    private sealed class InProcessDavTransport(IServiceScopeFactory scopes)
            : IGatewayApplicationTransport
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public async Task<TResponse> SendAsync<TRequest, TResponse>(
            string protocol,
            string operation,
            TRequest value,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("dav", protocol, StringComparer.Ordinal);
            using var scope = scopes.CreateScope();
            var now = DateTimeOffset.UtcNow;
            var request = new ApplicationRequest(
                Guid.CreateVersion7(), Guid.CreateVersion7(), 0, protocol, operation,
                "application/json", JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
                new Dictionary<string, string>(StringComparer.Ordinal), now, now.AddMinutes(1));
            var response = await new ApplicationRequestDispatcher(scope.ServiceProvider)
                .DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            Assert.IsFalse(response.IsError, response.ErrorDetail);
            return JsonSerializer.Deserialize<TResponse>(response.Payload, JsonOptions)
                ?? throw new AssertFailedException("The DAV application response was empty.");
        }
    }
}
