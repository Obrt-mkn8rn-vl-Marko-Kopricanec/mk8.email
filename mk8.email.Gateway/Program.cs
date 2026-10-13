using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using mk8.email.Configuration;
using mk8.email.Gateway;
using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Imap;
using mk8.email.Contracts.Pop3;
using mk8.email.Contracts.Sieve;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols;
using mk8.email.Gateway.Protocols.Autodiscover;
using mk8.email.Gateway.Protocols.Ews;
using mk8.email.Gateway.Protocols.Dav;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Gateway.Protocols.Imap;
using mk8.email.Gateway.Protocols.OAuth;
using mk8.email.Gateway.Protocols.Pop3;
using mk8.email.Gateway.Protocols.Sieve;
using mk8.email.Gateway.Security;
using mk8.email.Hosting;
using mk8.email.Imap.Presentation;
using mk8.email.Messaging;
using mk8.email.Smtp.Presentation;

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync("The Gateway does not accept service configuration arguments.").ConfigureAwait(false);
    return 2;
}
EnvironmentConfig environmentConfig;
WebApplicationBuilder builder;
try
{
    environmentConfig = await PortableEnvironmentLoader.LoadAsync(EnvironmentValidationRole.Gateway).ConfigureAwait(false);
    builder = GatewayHostBootstrap.CreateBuilder(environmentConfig);
}
// Startup diagnostics must not print a malformed secret document or raw value.
#pragma warning disable CA1031
catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
#pragma warning restore CA1031
{
    await Console.Error.WriteLineAsync($"Gateway configuration failed ({exception.GetType().Name}).").ConfigureAwait(false);
    return 1;
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestHeadersTotalSize = GatewayHttpPayloadBudget.RequestHeadersBytes;
    options.Limits.MaxRequestLineSize = GatewayHttpPayloadBudget.RequestLineBytes;
    options.Limits.MaxRequestBodySize = environmentConfig.Jmap.EnableJmap
        ? Math.Max(
            64 * 1024,
            Math.Max(
                environmentConfig.Jmap.MaxRequestSizeBytes,
                environmentConfig.Jmap.MaxUploadSizeBytes))
        : 64 * 1024;
    if (environmentConfig.Dav.EnableDav)
        options.Limits.MaxRequestBodySize = Math.Max(
            options.Limits.MaxRequestBodySize.Value,
            Math.Max(1_048_576, environmentConfig.Dav.MaxResourceSizeBytes));
});

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddDistributedMessaging(environmentConfig);
builder.Services.AddGatewayApplicationClient();
builder.Services.AddSingleton<ISmtpApplicationService, GatewaySmtpApplicationService>();
builder.Services.AddSingleton<IPop3ApplicationService, GatewayPop3ApplicationService>();
builder.Services.AddSingleton<IImapApplicationService, GatewayImapApplicationService>();
if (environmentConfig.Imap.EnableImap || environmentConfig.Imap.EnableImplicitTls)
{
    builder.Services.AddHostedService(provider => new ImapServerService(
        provider.GetRequiredService<IServiceScopeFactory>(),
        environmentConfig,
        provider.GetRequiredService<ILogger<ImapServerService>>(),
        provider.GetRequiredService<IGatewayTrafficJournal>()));
}
builder.Services.AddSingleton<ISieveApplicationService, GatewaySieveApplicationService>();
builder.Services.AddHostedService(provider => new SmtpServerService(
    provider.GetRequiredService<IServiceScopeFactory>(),
    environmentConfig,
    provider.GetRequiredService<ILogger<SmtpServerService>>(),
    provider.GetRequiredService<IGatewayTrafficJournal>()));
if (environmentConfig.Sieve.EnableManageSieve)
{
    builder.Services.AddHostedService(provider => new ManageSieveServerService(
        provider.GetRequiredService<IServiceScopeFactory>(),
        environmentConfig,
        provider.GetRequiredService<ILogger<ManageSieveServerService>>(),
        provider.GetRequiredService<IGatewayTrafficJournal>()));
}
if (environmentConfig.Pop3.EnablePop3 || environmentConfig.Pop3.EnableImplicitTls)
{
    builder.Services.AddHostedService(provider => new Pop3ServerService(
        provider.GetRequiredService<IServiceScopeFactory>(),
        environmentConfig,
        provider.GetRequiredService<ILogger<Pop3ServerService>>(),
        provider.GetRequiredService<IPop3MaildropLeaseStore>(),
        provider.GetRequiredService<IGatewayTrafficJournal>()));
}
builder.Services.AddOutboundSmtpPresentation();
if (environmentConfig.Dav.EnableDav)
    builder.Services.AddScoped<GatewayDavStore>();
builder.Services.AddSingleton<GatewayWebPushService>();
builder.Services.AddHostedService<GatewayPresentationWorker>();
builder.Services.AddSingleton(environmentConfig);
if (environmentConfig.OAuth.EnableOAuth)
    builder.Services.AddOAuthProtocol();
builder.Services.AddSingleton(environmentConfig.Admin);
builder.Services.AddSingleton<AdminNetworkPolicy>();
builder.Services.AddSingleton<IAdminAuditLog, AdminAuditLog>();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(environmentConfig.Admin.DataProtectionKeyPath))
    .SetApplicationName("mk8.email.admin");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-mk8admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(environmentConfig.Admin.SessionMinutes);
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login";
        options.SlidingExpiration = false;
    });

builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-mk8admin-xsrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
});

var app = builder.Build();
await using var appLifetime = app.ConfigureAwait(false);

GatewayHttpPipeline.Configure(app);

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (
    IApplicationTransportControl transport,
    CancellationToken cancellationToken) =>
{
    var ready = await transport.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
    return ready ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
}).AllowAnonymous();
app.MapGet("/health/application", GatewayApplicationHealth.CheckAsync).AllowAnonymous();
app.MapAutodiscoverEndpoints();
app.MapEwsEndpoints();
if (environmentConfig.OAuth.EnableOAuth)
    app.MapOAuthEndpoints();
if (environmentConfig.Jmap.EnableJmap)
    app.MapJmapEndpoints();
if (environmentConfig.Dav.EnableDav)
    app.MapDavEndpoints();
app.MapRazorPages();

await app.RunAsync().ConfigureAwait(false);
return 0;

public partial class Program;
