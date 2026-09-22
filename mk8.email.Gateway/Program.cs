using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Gateway.Security;

var builder = WebApplication.CreateBuilder(args);
var environmentConfig = EnvironmentLoader.Load(builder.Environment.IsDevelopment());

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 64 * 1024;
});

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

builder.Services.AddInfrastructure(environmentConfig);
builder.Services.AddApplication();
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

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
    if (!await database.Database.CanConnectAsync())
        throw new InvalidOperationException("The administration database is not available.");
    if (!await database.GlobalConfig.AnyAsync())
        throw new InvalidOperationException("The administration database schema is not initialized.");
    if (!await database.Users.AnyAsync(user => user.Role == "SuperAdmin" && user.IsActive))
        throw new InvalidOperationException("The database does not contain an active SuperAdmin account.");

    await scope.ServiceProvider.GetRequiredService<ISeederService>().SeedAsync();
}

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseForwardedHeaders();
app.UseMiddleware<AdminNetworkMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";
    context.Response.Headers["Cache-Control"] = "no-store";
    await next(context);
});
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (EmailDbContext database, CancellationToken cancellationToken) =>
{
    var ready = await database.Database.CanConnectAsync(cancellationToken)
        && await database.Users.AnyAsync(
            user => user.Role == "SuperAdmin" && user.IsActive,
            cancellationToken);
    return ready ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
}).AllowAnonymous();
app.MapRazorPages();

await app.RunAsync();

public partial class Program;
