using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class JmapApplicationService(
    IMailAuthenticator mailAuthenticator,
    IServiceProvider services,
    JmapAccountProfileService sessions,
    JmapRequestProcessor processor,
    JmapAccountService accounts,
    JmapBlobService blobs,
    JmapStateChangeService stateChanges,
    JmapConcurrencyLimiter concurrency,
    EnvironmentConfig environment) : IJmapApplicationService
{
    public async Task<JmapApplicationResult> GetProfileAsync(
        JmapProfileApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(request.Authentication, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Unauthorized();

        var profile = await sessions.GetProfileAsync(user, cancellationToken).ConfigureAwait(false);
        return new JmapApplicationResult(JmapApplicationOutcomes.Ok, Profile: profile);
    }

    public async Task<JmapApplicationResult> ValidatePlanAsync(
        MailPlanApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(request.Authentication, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Unauthorized();

        var profile = await sessions.GetProfileAsync(user, cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await concurrency.AcquireRequestAsync(cancellationToken).ConfigureAwait(false);
            processor.ValidatePreflight(request.Plan);
            return new JmapApplicationResult(JmapApplicationOutcomes.Ok, Profile: profile);
        }
        catch (MailApplicationException exception)
        {
            return new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                Failure: exception.Failure, Profile: profile);
        }
    }

    public async Task<JmapApplicationResult> ExecuteOperationAsync(
        MailOperationApplicationRequest request,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(request.Authentication, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Unauthorized();
        try
        {
            using var lease = await concurrency.AcquireRequestAsync(cancellationToken).ConfigureAwait(false);
            var result = await processor.ExecuteAsync(request.Command, user, operationId, cancellationToken).ConfigureAwait(false);
            return new JmapApplicationResult(JmapApplicationOutcomes.Ok, OperationResult: result);
        }
        catch (MailApplicationException exception)
        {
            return new JmapApplicationResult(JmapApplicationOutcomes.Ok, Failure: exception.Failure);
        }
    }

    public async Task<JmapApplicationResult> UploadAsync(
        JmapUploadApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(request.Authentication, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Unauthorized();
        if (request.Content.LongLength > environment.Jmap.MaxUploadSizeBytes)
        {
            return new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                Failure: new MailApplicationFailure(MailFailureKind.ResourceLimit,
                    "Content is larger than the storage admission limit.", MailResourceLimit.UploadSize));
        }

        var account = await accounts.GetAccountAsync(user, request.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return NotFound();

        try
        {
            using var lease = await concurrency.AcquireUploadAsync(cancellationToken).ConfigureAwait(false);
            var contentType = JmapMediaType.TryNormalize(request.ContentType, out var normalized)
                ? normalized
                : "application/octet-stream";
            var stored = await blobs.StoreAsync(
                account.InboxId,
                request.Content,
                contentType,
                null,
                cancellationToken).ConfigureAwait(false);
            return new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                ContentType: contentType,
                BlobId: stored.BlobId,
                Size: stored.SizeBytes);
        }
        catch (MailApplicationException exception)
        {
            return new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                Failure: exception.Failure);
        }
    }

    public async Task<JmapApplicationResult> DownloadAsync(
        JmapDownloadApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(request.Authentication, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Unauthorized();
        var account = await accounts.GetAccountAsync(user, request.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return NotFound();
        var blob = await blobs.GetAsync(account.InboxId, request.BlobId, cancellationToken).ConfigureAwait(false);
        return blob is null
            ? NotFound()
            : new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                blob.Content,
                blob.ContentType,
                Size: blob.Content.LongLength);
    }

    public async Task<JmapApplicationResult> PollChangesAsync(
        JmapChangesApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(request.Authentication, cancellationToken).ConfigureAwait(false);
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
                    Failure: new MailApplicationFailure(MailFailureKind.InvalidSelection));
            }
            types = requested;
        }

        if (request.AfterCursor is null)
        {
            var cursor = await stateChanges.GetCursorAsync(user, cancellationToken).ConfigureAwait(false);
            return new JmapApplicationResult(JmapApplicationOutcomes.Ok, Cursor: cursor);
        }

        var poll = await stateChanges.PollAsync(
            user,
            request.AfterCursor.Value,
            types,
            cancellationToken).ConfigureAwait(false);
        return new JmapApplicationResult(JmapApplicationOutcomes.Ok, Cursor: poll.Cursor, Changes: poll.Changes);
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
                cancellationToken).ConfigureAwait(false);
        }

        if (authentication.Kind == ProtocolAuthenticationKinds.BearerToken
            && environment.OAuth.EnableOAuth)
        {
            return await services.GetRequiredService<IOAuthTokenService>()
                .AuthenticateAccessTokenAsync(
                authentication.Secret,
                "jmap",
                cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static JmapApplicationResult Unauthorized() =>
        new(JmapApplicationOutcomes.Unauthorized);

    private static JmapApplicationResult NotFound() =>
        new(JmapApplicationOutcomes.NotFound);
}
