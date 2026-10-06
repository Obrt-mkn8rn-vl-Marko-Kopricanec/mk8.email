using Microsoft.Net.Http.Headers;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapEwsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/ews/exchange.asmx", ["GET", "HEAD", "POST"], InvokeAsync);
        return endpoints;
    }

    private static async Task InvokeAsync(HttpContext context, GatewayEwsClient application,
        EnvironmentConfig environment, GatewayJmapBatchLimiter limiter, GatewayApplicationOptions options,
        CancellationToken cancellationToken)
    {
        if (!context.Request.IsHttps)
        {
            await FaultAsync(context, "ErrorAccessDenied", StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }
        if (!HttpMethods.IsPost(context.Request.Method)
            || !MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase))
        {
            await FaultAsync(context, "ErrorInvalidRequest", StatusCodes.Status415UnsupportedMediaType).ConfigureAwait(false);
            return;
        }
        try
        {
            var request = await GatewayEwsRequestParser.ReadAsync(context.Request.Body, cancellationToken,
                mediaType.Charset.ToString().Trim('"')).ConfigureAwait(false);
            var actions = context.Request.Headers["SOAPAction"];
            if (actions.Count > 1) throw new GatewayEwsRequestException("ErrorInvalidRequest");
            var action = actions.ToString().Trim();
            if (action.Length >= 2 && action[0] == '"' && action[^1] == '"') action = action[1..^1];
            if (action.Contains('"', StringComparison.Ordinal)) throw new GatewayEwsRequestException("ErrorInvalidRequest");
            if (action.Length != 0 && !string.Equals(action, $"{GatewayEwsSoap.Messages}/{request.Operation}", StringComparison.Ordinal))
                throw new GatewayEwsRequestException("ErrorInvalidRequest");
            if (!GatewayJmapAuthentication.TryParse(context.Request, environment, out var authentication)
                || !string.Equals(authentication.Kind, ProtocolAuthenticationKinds.Password, StringComparison.Ordinal))
            {
                await UnauthorizedAsync(context).ConfigureAwait(false);
                return;
            }
            using var lease = limiter.TryAcquire(environment.Jmap.MaxConcurrentRequests);
            if (lease is null)
            {
                await FaultAsync(context, "ErrorServerBusy", StatusCodes.Status503ServiceUnavailable).ConfigureAwait(false);
                return;
            }
            await ExecuteAsync(context, application, authentication, request, options, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayEwsRequestException exception)
        {
            if (exception.Status == StatusCodes.Status401Unauthorized)
                await UnauthorizedAsync(context).ConfigureAwait(false);
            else
            {
                if (exception.Status == StatusCodes.Status503ServiceUnavailable) context.Response.Headers.RetryAfter = "5";
                await GatewayEwsSoap.WriteAsync(context, context.Response.Body,
                    GatewayEwsSoap.Fault(exception.Code, "The EWS request is invalid or unsupported.", exception.FaultPrefix, exception.IsHeaderFault), exception.Status)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteAsync(HttpContext context, GatewayEwsClient application,
        ProtocolAuthentication authentication, GatewayEwsRequest request, GatewayApplicationOptions options,
        CancellationToken cancellationToken)
    {
        using var deadline = GatewayApplicationDeadline.Begin(options.RequestTimeout);
        var profile = await application.AuthenticateAsync(authentication, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            await UnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }
        var primary = profile.Accounts.Where(account => string.Equals(account.Name, profile.Username, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (primary.Length > 1) throw new InvalidOperationException("The EWS primary account is ambiguous.");
        var accountId = Guid.Empty;
        var read = primary.Length == 0 ? null : await application.ReadGraphAsync(authentication, profile,
            GatewayEwsClient.TryAccount(primary[0].Id, out accountId) ? accountId : throw new InvalidOperationException("Invalid EWS account."),
            cancellationToken).ConfigureAwait(false);
        var graph = read?.Status == MailFolderReadStatus.Ok ? new GatewayEwsFolderGraph(accountId, read.State!, read.Folders) : null;
        var error = read?.Status == MailFolderReadStatus.RequestTooLarge ? "ErrorExceededFindCountLimit" : "ErrorFolderNotFound";
        await GatewayEwsSoap.WriteAsync(context, context.Response.Body,
            GatewayEwsFolderResponse.Render(request, graph, profile.Username, error)).ConfigureAwait(false);
    }

    private static Task UnauthorizedAsync(HttpContext context)
    {
        context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"mk8.email EWS\", charset=\"UTF-8\"");
        return FaultAsync(context, "ErrorAccessDenied", StatusCodes.Status401Unauthorized);
    }

    private static Task FaultAsync(HttpContext context, string code, int status)
    {
        if (status == StatusCodes.Status503ServiceUnavailable) context.Response.Headers.RetryAfter = "5";
        return GatewayEwsSoap.WriteAsync(context, context.Response.Body,
            GatewayEwsSoap.Fault(code, "The EWS request could not be completed."), status);
    }
}
