using System.Net;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using mk8.email.Configuration;

namespace mk8.email.Gateway;

internal static class GatewayHostBootstrap
{
    internal static WebApplicationBuilder CreateBuilder(EnvironmentConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = configuration.Http.Validate();
        if (errors.Count != 0)
            throw new InvalidOperationException("Gateway HTTP configuration is invalid: " + string.Join("; ", errors));
        RequireConsistentListenerPorts(configuration);
        string[] allowedHosts = [.. configuration.Http.AllowedHosts];

        // Construction itself must not consume JSON, process environment, CLI
        // service values or a current-working-directory bootstrap input.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(GatewayHostBootstrap).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseKestrel(options =>
            options.Listen(IPAddress.Parse(configuration.Http.ListenAddress), configuration.Http.Port));
        builder.Services.AddRouting();
        builder.Services.AddHostFiltering(options =>
        {
            options.AllowedHosts = allowedHosts;
            options.AllowEmptyHosts = false;
            options.IncludeFailureMessage = false;
        });
        builder.Services.AddHttpsRedirection(options => options.HttpsPort = configuration.Http.HttpsRedirectPort);
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            // These exact loopback peers are the existing trusted-hop contract,
            // not a deployment subnet or a trust grant to another guest.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        });
        return builder;
    }

    private static void RequireConsistentListenerPorts(EnvironmentConfig configuration)
    {
        var listeners = new (bool Enabled, int Port)[]
        {
            (configuration.Smtp.EnableSmtp, configuration.Smtp.Port),
            (configuration.Smtp.EnableSubmission, configuration.Smtp.SubmissionPort),
            (configuration.Smtp.EnableImplicitTls, configuration.Smtp.ImplicitTlsPort),
            (configuration.Imap.EnableImap, configuration.Imap.Port),
            (configuration.Imap.EnableImplicitTls, configuration.Imap.ImplicitTlsPort),
            (configuration.Pop3.EnablePop3, configuration.Pop3.Port),
            (configuration.Pop3.EnableImplicitTls, configuration.Pop3.ImplicitTlsPort),
            (configuration.Sieve.EnableManageSieve, configuration.Sieve.Port),
        };
        if (listeners.Any(listener => listener.Enabled && listener.Port == configuration.Http.Port))
            throw new InvalidOperationException("Http.Port cannot conflict with an enabled native listener.");
        if ((configuration.Jmap.EnableJmap || configuration.Dav.EnableDav || configuration.OAuth.EnableOAuth)
            && configuration.Jmap.Port != configuration.Http.Port)
        {
            throw new InvalidOperationException("Jmap.Port must agree with Http.Port for the shared HTTP listener.");
        }
    }
}
