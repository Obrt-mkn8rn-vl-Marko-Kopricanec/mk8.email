using System.Text;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class JmapApplicationService(
    IMailAuthenticator mailAuthenticator,
    IServiceProvider services,
    JmapSessionService sessions,
    JmapRequestProcessor processor,
    JmapAccountService accounts,
    JmapBlobService blobs,
    JmapStateChangeService stateChanges,
    JmapConcurrencyLimiter concurrency,
    EnvironmentConfig environment) : IJmapApplicationService
{
    public async Task<JmapApplicationResult> GetSessionAsync(
        JmapSessionApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(request.Authentication, cancellationToken);
        if (user is null)
            return Unauthorized();

        var session = await sessions.BuildAsync(user, cancellationToken);
        return Json(session.Value.ToJsonString(JmapJson.SerializerOptions));
    }

    public async Task<JmapApplicationResult> ProcessApiRequestAsync(
        JmapApiApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(request.Authentication, cancellationToken);
        if (user is null)
            return Unauthorized();

        try
        {
            using var lease = await concurrency.AcquireRequestAsync(cancellationToken);
            var document = JmapJson.ParseRequest(request.Document, environment.Jmap);
            var response = await processor.ProcessAsync(document, user, cancellationToken);
            return Json(response.ToJsonString(JmapJson.SerializerOptions));
        }
        catch (JmapRequestException exception)
        {
            return new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                Problem: new JmapApplicationProblem(
                    exception.Type,
                    exception.Title,
                    exception.Detail,
                    exception.Limit));
        }
    }

    public async Task<JmapApplicationResult> UploadAsync(
        JmapUploadApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(request.Authentication, cancellationToken);
        if (user is null)
            return Unauthorized();
        if (request.Content.LongLength > environment.Jmap.MaxUploadSizeBytes)
        {
            return new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                Problem: new JmapApplicationProblem(
                    "urn:ietf:params:jmap:error:limit",
                    "Upload failed",
                    "Upload is larger than the server limit.",
                    "maxSizeUpload"));
        }

        var account = await accounts.GetAccountAsync(user, request.AccountId, cancellationToken);
        if (account is null)
            return NotFound();

        try
        {
            using var lease = await concurrency.AcquireUploadAsync(cancellationToken);
            var contentType = JmapMediaType.TryNormalize(request.ContentType, out var normalized)
                ? normalized
                : "application/octet-stream";
            var stored = await blobs.StoreAsync(
                account.InboxId,
                request.Content,
                contentType,
                null,
                cancellationToken);
            return new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                ContentType: contentType,
                BlobId: stored.BlobId,
                Size: stored.SizeBytes);
        }
        catch (JmapRequestException exception)
        {
            return new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                Problem: new JmapApplicationProblem(
                    exception.Type,
                    exception.Title,
                    exception.Detail,
                    exception.Limit));
        }
    }

    public async Task<JmapApplicationResult> DownloadAsync(
        JmapDownloadApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(request.Authentication, cancellationToken);
        if (user is null)
            return Unauthorized();
        var account = await accounts.GetAccountAsync(user, request.AccountId, cancellationToken);
        if (account is null)
            return NotFound();
        var blob = await blobs.GetAsync(account.InboxId, request.BlobId, cancellationToken);
        return blob is null
            ? NotFound()
            : new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                blob.Content,
                blob.ContentType,
                Size: blob.Content.LongLength);
    }

    public async Task<JmapApplicationResult> PollEventAsync(
        JmapEventApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(request.Authentication, cancellationToken);
        if (user is null)
            return Unauthorized();

        IReadOnlySet<string>? types = null;
        if (request.Types is not null)
        {
            var requested = request.Types.ToHashSet(StringComparer.Ordinal);
            if (requested.Count != request.Types.Length
                || requested.Any(type => !JmapStateChangeService.SupportedTypes.Contains(type)))
            {
                return new JmapApplicationResult(
                    JmapApplicationOutcomes.Ok,
                    Problem: new JmapApplicationProblem(
                        "urn:ietf:params:jmap:error:invalidArguments",
                        "Invalid event source parameters"));
            }
            types = requested;
        }

        if (request.AfterCursor is null)
        {
            var cursor = await stateChanges.GetCursorAsync(user, cancellationToken);
            return new JmapApplicationResult(JmapApplicationOutcomes.Ok, Cursor: cursor);
        }

        var poll = await stateChanges.PollAsync(
            user,
            request.AfterCursor.Value,
            types,
            cancellationToken);
        return new JmapApplicationResult(
            JmapApplicationOutcomes.Ok,
            poll.StateChange is null
                ? null
                : Encoding.UTF8.GetBytes(
                    poll.StateChange.ToJsonString(JmapJson.SerializerOptions)),
            "application/json",
            Cursor: poll.Cursor);
    }

    private async Task<AuthenticatedMailUser?> AuthenticateAsync(
        ProtocolAuthentication authentication,
        CancellationToken cancellationToken)
    {
        if (authentication.Kind == ProtocolAuthenticationKinds.Password
            && !string.IsNullOrEmpty(authentication.Username))
        {
            return await mailAuthenticator.AuthenticateAsync(
                authentication.Username,
                authentication.Secret,
                cancellationToken);
        }

        if (authentication.Kind == ProtocolAuthenticationKinds.BearerToken
            && environment.OAuth.EnableOAuth)
        {
            return await services.GetRequiredService<IOAuthTokenService>()
                .AuthenticateAccessTokenAsync(
                authentication.Secret,
                "jmap",
                cancellationToken);
        }

        return null;
    }

    private static JmapApplicationResult Json(string value) => new(
        JmapApplicationOutcomes.Ok,
        Encoding.UTF8.GetBytes(value),
        "application/json");

    private static JmapApplicationResult Unauthorized() =>
        new(JmapApplicationOutcomes.Unauthorized);

    private static JmapApplicationResult NotFound() =>
        new(JmapApplicationOutcomes.NotFound);
}
