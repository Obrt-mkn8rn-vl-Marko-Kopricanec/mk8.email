using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

internal sealed class DavFixture : IAsyncDisposable
{
    private const string Username = "dav.user@mk8n.com";
    private const string Password = "correct horse battery staple";
    private readonly WebApplication application;

    private DavFixture(WebApplication application, HttpClient client, Guid userId)
    {
        this.application = application;
        Client = client;
        UserId = userId;
    }

    public HttpClient Client { get; }
    public Guid UserId { get; }
    public string PrincipalPath => $"/dav/principals/{UserId:N}/";
    public string CalendarHomePath => $"/dav/calendars/{UserId:N}/";
    public string AddressBookHomePath => $"/dav/addressbooks/{UserId:N}/";

    public static async Task<DavFixture> CreateAsync()
    {
        var configuration = new EnvironmentConfig
        {
            Dav = new DavConfig
            {
                EnableDav = true,
                MaxResourceSizeBytes = 1_048_576,
                MaxCollectionsPerUser = 20,
                MaxResourcesPerCollection = 1_000,
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
        builder.Services.AddDavProtocol();

        var application = builder.Build();
        application.MapDavEndpoints();

        var userId = Guid.CreateVersion7();
        using (var scope = application.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "DAV Protocol Test",
            };
            database.Addresses.Add(new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "mk8n.com",
                Company = company,
                IsActive = true,
            });
            database.Users.Add(new UserDB
            {
                Id = userId,
                Username = Username,
                PasswordHash = PasswordHasher.Hash(Password),
                Role = "User",
                Company = company,
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
        return new DavFixture(application, client, userId);
    }

    public Task<HttpResponseMessage> SendAsync(
        string method,
        string path,
        string? body = null,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? headers = null,
        bool authenticate = true)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (authenticate)
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{Username}:{Password}"));
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
}
