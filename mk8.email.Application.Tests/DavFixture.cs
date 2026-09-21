using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

internal sealed class DavFixture : IAsyncDisposable
{
    private const string Username = "dav.user@mk8n.com";
    private const string AttendeeUsername = "dav.attendee@mk8n.com";
    private const string OutsiderUsername = "dav.outsider@other.example";
    private const string Password = "correct horse battery staple";
    private readonly WebApplication application;
    private readonly CapturingMailSubmissionQueue submissionQueue;

    private DavFixture(
        WebApplication application,
        HttpClient client,
        Guid userId,
        Guid inboxId,
        Guid attendeeUserId,
        Guid outsiderUserId,
        CapturingMailSubmissionQueue submissionQueue)
    {
        this.application = application;
        this.submissionQueue = submissionQueue;
        Client = client;
        UserId = userId;
        InboxId = inboxId;
        AttendeeUserId = attendeeUserId;
        OutsiderUserId = outsiderUserId;
    }

    public HttpClient Client { get; }
    public IServiceProvider Services => application.Services;
    public Guid UserId { get; }
    public Guid InboxId { get; }
    public Guid AttendeeUserId { get; }
    public Guid OutsiderUserId { get; }
    public string PrimaryAddress => Username;
    public string AccountId => JmapId.Account(InboxId);
    public string AttendeeAddress => AttendeeUsername;
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
    public IReadOnlyList<MailSubmission> QueuedSubmissions => submissionQueue.Submissions;

    public static async Task<DavFixture> CreateAsync()
    {
        var configuration = new EnvironmentConfig
        {
            Smtp = new SmtpConfig
            {
                Hostname = "email.mk8n.com",
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
                PublicBaseUrl = "https://email.mk8n.com",
            },
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(configuration);
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = $"dav-{Guid.NewGuid():N}";
        builder.Services.AddDbContext<EmailDbContext>(options =>
            options.UseInMemoryDatabase(databaseName, databaseRoot));
        builder.Services.AddScoped<IMailAuthenticator, MailAuthenticator>();
        var submissionQueue = new CapturingMailSubmissionQueue();
        builder.Services.AddSingleton<IMailSubmissionQueue>(submissionQueue);
        builder.Services.AddDavProtocol();
        builder.Services.AddJmapProtocol();

        var application = builder.Build();
        application.MapDavEndpoints();
        application.MapJmapEndpoints();

        var userId = Guid.CreateVersion7();
        var inboxId = Guid.CreateVersion7();
        var attendeeUserId = Guid.CreateVersion7();
        var outsiderUserId = Guid.CreateVersion7();
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
                Domain = "mk8n.com",
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
            database.Addresses.Add(primaryAddress);
            database.Users.Add(primaryUser);
            database.Inboxes.Add(new InboxDB
            {
                Id = inboxId,
                Name = "dav.user",
                Address = primaryAddress,
                Owner = primaryUser,
            });
            database.Users.Add(new UserDB
            {
                Id = attendeeUserId,
                Username = AttendeeUsername,
                PasswordHash = PasswordHasher.Hash(Password),
                Role = "User",
                Company = company,
            });
            var outsiderCompany = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "Other DAV Tenant",
            };
            database.Addresses.Add(new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "other.example",
                Company = outsiderCompany,
                IsActive = true,
            });
            database.Users.Add(new UserDB
            {
                Id = outsiderUserId,
                Username = OutsiderUsername,
                PasswordHash = PasswordHasher.Hash(Password),
                Role = "User",
                Company = outsiderCompany,
            });
            await database.SaveChangesAsync();
        }
        using (var verificationScope = application.Services.CreateScope())
        {
            var seededAuthentication = await verificationScope.ServiceProvider
                .GetRequiredService<IMailAuthenticator>()
                .AuthenticateAsync(Username, Password);
            Assert.IsNotNull(seededAuthentication, "The DAV fixture account must authenticate from a fresh service scope.");
        }

        await application.StartAsync();
        var addresses = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = AssertExactlyOne(addresses);
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(15),
        };
        return new DavFixture(
            application,
            client,
            userId,
            inboxId,
            attendeeUserId,
            outsiderUserId,
            submissionQueue);
    }

    public async Task<JsonObject> SendJmapAsync(JsonObject request)
    {
        using var response = await SendAsync(
            "POST",
            "/jmap/api",
            request.ToJsonString(JmapJson.SerializerOptions),
            "application/json");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsInstanceOfType<JsonObject>(
            JsonNode.Parse(await response.Content.ReadAsStringAsync()));
    }

    public async Task<string> StoreBlobAsync(byte[] content, string contentType)
    {
        using var scope = Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .StoreAsync(InboxId, content, contentType, null, CancellationToken.None);
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

    private Task<HttpResponseMessage> SendAsAsync(
        string username,
        string method,
        string path,
        string? body,
        string? contentType,
        IReadOnlyDictionary<string, string>? headers,
        bool authenticate)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
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
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
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
}
