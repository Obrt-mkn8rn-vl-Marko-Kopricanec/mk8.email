using mk8.email.Configuration;
using mk8.email.Contracts.Imap;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Gateway.Protocols.Autodiscover;

internal static class GatewayAutodiscoverEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAutodiscoverEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/autodiscover/autodiscover.xml", ["GET", "HEAD", "POST"], DiscoverAsync);
        return endpoints;
    }

    private static async Task DiscoverAsync(HttpContext context, IImapApplicationService application,
        EnvironmentConfig environment, GatewayApplicationOptions options, CancellationToken cancellationToken)
    {
        if (!context.Request.IsHttps)
        {
            await WriteErrorAsync(context, 600, "TLS is required.", StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await WriteErrorAsync(context, 600, "Invalid Request.").ConfigureAwait(false);
            return;
        }
        var request = await GatewayAutodiscoverXml.ReadAsync(context.Request.Body, cancellationToken).ConfigureAwait(false);
        if (request.ErrorCode != 0)
        {
            await WriteErrorAsync(context, request.ErrorCode,
                request.ErrorCode == 601 ? "The requested configuration type is not supported." : "Invalid Request.")
                .ConfigureAwait(false);
            return;
        }
        if (!GatewayJmapAuthentication.TryParse(context.Request, environment, out var authentication)
            || !string.Equals(authentication.Kind, ProtocolAuthenticationKinds.Password, StringComparison.Ordinal))
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }
        await WriteAccountSettingsAsync(context, application, authentication, request.EmailAddress!, environment,
            options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAccountSettingsAsync(HttpContext context, IImapApplicationService application,
        ProtocolAuthentication authentication, string emailAddress, EnvironmentConfig environment,
        GatewayApplicationOptions options, CancellationToken cancellationToken)
    {
        using var deadline = GatewayApplicationDeadline.Begin(options.RequestTimeout);
        var identity = await application.AuthenticatePasswordAsync(
            new ImapPasswordAuthentication(authentication.Username!, authentication.Secret), cancellationToken).ConfigureAwait(false);
        if (identity.UserId is null || identity.UserId == Guid.Empty || string.IsNullOrEmpty(identity.Username))
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(identity.Username, emailAddress, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, 500, "The email address cannot be found.").ConfigureAwait(false);
            return;
        }
        var mailboxes = await application.ListMailboxesAsync(
            new ImapMailboxListRequest(identity.UserId.Value, SubscribedOnly: false), cancellationToken).ConfigureAwait(false);
        if (!mailboxes.Mailboxes.Any(mailbox => string.Equals(
                $"{mailbox.InboxName}@{mailbox.Domain}", emailAddress, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteErrorAsync(context, 500, "The email address cannot be found.").ConfigureAwait(false);
            return;
        }
        await GatewayAutodiscoverXml.WriteAsync(context, context.Response.Body,
            GatewayAutodiscoverXml.Settings(identity.Username, environment)).ConfigureAwait(false);
    }

    private static Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"mk8.email Autodiscover\", charset=\"UTF-8\"");
        return WriteErrorAsync(context, 500, "Authentication required.", StatusCodes.Status401Unauthorized);
    }

    private static Task WriteErrorAsync(HttpContext context, int code, string message, int status = StatusCodes.Status200OK) =>
        GatewayAutodiscoverXml.WriteAsync(context, context.Response.Body, GatewayAutodiscoverXml.Error(code, message), status);
}
