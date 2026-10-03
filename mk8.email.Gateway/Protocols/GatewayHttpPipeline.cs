using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Security;

namespace mk8.email.Gateway.Protocols;

internal static class GatewayHttpPipeline
{
    public static void Configure(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            app.UseWhen(context => !GatewayProtocolPaths.IsPublicProtocol(context.Request.Path),
                branch => branch.UseExceptionHandler("/Error"));
        app.UseMiddleware<GatewayProtocolTrafficCaptureMiddleware>();
        app.UseMiddleware<GatewayApplicationFailureMiddleware>();
        app.UseForwardedHeaders();
        app.UseWhen(context => !GatewayProtocolPaths.IsPublicProtocol(context.Request.Path),
            branch => branch.UseMiddleware<AdminNetworkMiddleware>());
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";
            context.Response.Headers.CacheControl = "no-store";
            await next(context).ConfigureAwait(false);
        });
        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
    }
}
